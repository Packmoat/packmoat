using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The image bakes some settings as environment variables (ENV in dotnet/Dockerfile), such as PACKMOAT_DB, a SQLite path. The server
/// refuses to start when a setting and its _FILE form are both set, so a container template that gives X_FILE for a baked X must clear X
/// first: its entry command is /usr/bin/env -u X, then what the image would have run (the entrypoint for the server, packmoat-admin for a
/// job). These tests read the Dockerfile and each template's container commands, with no Docker and no cloud, so the template and the
/// image cannot drift apart unseen. CI's image PostgreSQL smoke runs the same command on the built image.
/// </summary>
public class ContainerTemplateEntryTests
{
    // Every file of each template folder (a folder under the core's root) that has a container image line is checked; a file with none is
    // skipped. A glob, not a list, so a container added in a new file cannot be missed.
    private static readonly (string[] Folder, string Pattern)[] TemplateFolders =
    [
        (["deploy", "azure-container-apps"], "*.bicep"),
        (["deploy", "cloud-run"], "*.tf"),
        (["deploy", "aws-ecs"], "*.tf"),
    ];

    // A container's entry command: `command` (bicep, Cloud Run) or `entryPoint` (ECS), at the start of a line, as a list of quoted items.
    private static readonly Regex Entry = new(@"(?m)^[ \t]*(?<key>entryPoint|command)\s*[:=]\s*\[(?<items>[^\]]*)\]", RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new(@"'(?<s>[^']*)'|""(?<s>[^""]*)""", RegexOptions.CultureInvariant);
    // Every container's image line, whatever it names: bicep's `image: checkedServiceImage` or `image: '${jobImage}'`, Terraform's
    // `image = var.service_image` or `image = local.x`, or the JSON form `"image": "..."`.
    private static readonly Regex ImageLine = new(@"(?m)^[ \t]*""?image""?\s*[:=]", RegexOptions.CultureInvariant);

    private static readonly Regex SecretsInitCommand = new(@"(?m)^[ \t]*command\s*=\s*\[\s*local\.secrets_init_script\s*\]", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentKey = new(@"(?m)^[ \t]*""?environment""?\s*[:=]", RegexOptions.CultureInvariant);
    private static readonly Regex SecretsInitScript = new(@"secrets_init_script\s*=\s*<<-?(?<tag>\w+)\r?\n(?<text>.*?)\r?\n[ \t]*\k<tag>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static string Dockerfile() => CoreRoot.Read("dotnet", "Dockerfile").Replace("\r\n", "\n");

    /// <summary>The names the Dockerfile's ENV instructions bake in.</summary>
    internal static string[] BakedNames(string dockerfile)
    {
        var names = new List<string>();
        foreach (var instruction in Regex.Matches(dockerfile, @"(?m)^ENV\b((?:[^\n]*\\\n)*[^\n]*)").Select(m => m.Groups[1].Value))
            names.AddRange(Regex.Matches(instruction.Replace("\\\n", " "), @"(?:^|\s)([A-Za-z_][A-Za-z0-9_]*)=").Select(m => m.Groups[1].Value));
        return [.. names];
    }

    internal static string[] Entrypoint(string dockerfile)
    {
        var line = Regex.Match(dockerfile, @"(?m)^ENTRYPOINT\s+(\[.*\])\s*$");
        Assert.True(line.Success, "dotnet/Dockerfile has no exec-form ENTRYPOINT.");
        return JsonSerializer.Deserialize<string[]>(line.Groups[1].Value)!;
    }

    private static string[] Items(Match entry) => Quoted.Matches(entry.Groups["items"].Value).Select(q => q.Groups["s"].Value).ToArray();

    /// <summary>The template files to check, each as (path under the core's root, its text): those in the template folders with an image line.</summary>
    internal static (string Name, string Text)[] TemplateFiles()
    {
        var root = CoreRoot.Path();
        return [.. TemplateFolders.SelectMany(t => Directory.GetFiles(System.IO.Path.Combine([root, .. t.Folder]), t.Pattern))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Name: System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f)))
            .Where(f => ImageLine.IsMatch(f.Text))];
    }

    /// <summary>
    /// One line per problem in a template file's containers; empty when each clears what it must. A container runs from its image line to
    /// the next one. A container needs the override when it sets X_FILE for a baked X: its own text names it, or the file's shared settings
    /// do and the container does not just run a shell. A container whose entry is /bin/bash (ECS's secrets-init) and that names no X_FILE
    /// itself is exempt.
    /// </summary>
    internal static string[] Problems(string name, string file, string dockerfile)
    {
        var baked = BakedNames(dockerfile);
        var entrypoint = Entrypoint(dockerfile);
        var problems = new List<string>();
        var needed = baked.Where(x => Regex.IsMatch(file, $@"\b{Regex.Escape(x)}_FILE\b")).ToArray();

        // The exempt container runs this script, so it must not start the server or an admin command, nor name a baked X_FILE.
        foreach (var script in SecretsInitScript.Matches(file).Select(m => m.Groups["text"].Value))
        {
            if (script.Contains("dotnet") || script.Contains("packmoat-admin") || baked.Any(x => Regex.IsMatch(script, $@"\b{Regex.Escape(x)}_FILE\b")))
                problems.Add($"{name}: local.secrets_init_script runs the image or names a baked setting's _FILE: only the secrets-init container may run it.");
        }

        var starts = ImageLine.Matches(file).Select(m => m.Index).ToArray();
        var unsets = new List<string>();
        for (var i = 0; i < starts.Length; i++)
        {
            var container = file[starts[i]..(i + 1 < starts.Length ? starts[i + 1] : file.Length)];
            var entries = Entry.Matches(container);
            var chosen = entries.FirstOrDefault(m => m.Groups["key"].Value == "entryPoint") ?? entries.FirstOrDefault();
            var command = chosen is null ? [] : Items(chosen);
            var label = $"{name}: container {i + 1}";
            // The one exemption: ECS's secrets-init, a shell running exactly the template's secrets script, with no environment of its own.
            var onlyAShell = command.Length > 0 && (command[0] is "/bin/bash" or "/bin/sh") && SecretsInitCommand.IsMatch(container) && !EnvironmentKey.IsMatch(container);

            var at = 0;
            var cleared = new List<string>();
            if (command.Length > 0 && command[0] == "/usr/bin/env")
                for (at = 1; at + 1 < command.Length && command[at] == "-u"; at += 2) cleared.Add(command[at + 1]);
            unsets.AddRange(cleared);
            foreach (var x in cleared.Where(x => !baked.Contains(x)))
                problems.Add($"{label} clears {x}, which the image no longer bakes in: remove the override.");
            if (onlyAShell || (needed.Length == 0 && cleared.Count == 0)) continue;
            if (cleared.Count == 0)
            {
                problems.Add($"{label} does not start with /usr/bin/env -u, yet {string.Join(", ", needed)}_FILE is set in {name}: {(command.Length == 0 ? "(no command)" : string.Join(' ', command))}");
                continue;
            }
            foreach (var x in needed.Where(x => !cleared.Contains(x)))
                problems.Add($"{label} gives {x}_FILE but does not clear {x}: {string.Join(' ', command)}");
            var tail = command[at..];
            if (!tail.SequenceEqual(entrypoint) && !tail.SequenceEqual(["packmoat-admin"]))
                problems.Add($"{label}: after env -u the command must be the image's entrypoint ({string.Join(' ', entrypoint)}) or packmoat-admin, not: {string.Join(' ', tail)}");
        }

        if (needed.Length == 0 && unsets.Count > 0)
            problems.Add($"{name}: it clears {string.Join(", ", unsets)} but gives no _FILE form of it: remove the override.");
        return [.. problems];
    }

    [Fact]   // mutations: ENTRYPOINT changed in dotnet/Dockerfile; the packmoat-admin line removed
    public void The_image_has_the_entrypoint_and_the_admin_command_the_templates_run()
    {
        var dockerfile = Dockerfile();
        Assert.Equal(["dotnet", "/app/host/Packmoat.Server.dll"], Entrypoint(dockerfile));
        Assert.Contains("> /usr/local/bin/packmoat-admin", dockerfile);
        Assert.Contains("PACKMOAT_DB", BakedNames(dockerfile));
    }

    [Fact]   // mutations: drop a template's `-u PACKMOAT_DB`; add a container (any image form) or a new template file; remove PACKMOAT_DB from the Dockerfile's ENV
    public void Each_template_container_that_gives_a_baked_settings_file_clears_the_baked_setting_first()
    {
        var dockerfile = Dockerfile();
        var all = TemplateFiles().SelectMany(f => Problems(f.Name, f.Text, dockerfile)).ToArray();
        Assert.True(all.Length == 0, string.Join("\n", all));
    }

    [Fact]   // mutations: a template file renamed or moved out of its folder, so the glob no longer finds it
    public void The_template_folders_yield_the_container_files_of_all_three_clouds()
    {
        var names = TemplateFiles().Select(f => f.Name).ToArray();
        foreach (var expected in new[] { "deploy/azure-container-apps/main.bicep", "deploy/cloud-run/service.tf", "deploy/cloud-run/jobs.tf", "deploy/aws-ecs/service.tf", "deploy/aws-ecs/migrate.tf" })
            Assert.Contains(expected, names);
    }

    [Fact]   // mutations: the ECS server's or migrate container's entryPoint changed (the entrypoint tokens tie to the Dockerfile's)
    public void The_ECS_server_and_migrate_containers_run_the_images_own_commands_after_the_clear()
    {
        var entrypoint = Entrypoint(Dockerfile());
        foreach (var (file, tail) in new[] { ("service.tf", entrypoint), ("migrate.tf", ["packmoat-admin"]) })
        {
            var expected = new[] { "/usr/bin/env", "-u", "PACKMOAT_DB" }.Concat(tail).ToArray();
            var entryPoints = Entry.Matches(CoreRoot.Read("deploy", "aws-ecs", file)).Where(m => m.Groups["key"].Value == "entryPoint").Select(Items).ToList();
            Assert.True(entryPoints.Any(c => c.SequenceEqual(expected)), $"deploy/aws-ecs/{file} has no container with entryPoint {string.Join(' ', expected)}.");
        }
    }

    [Fact]   // no real file: the checks themselves, on a made-up template
    public void The_check_flags_a_missing_clear_a_wrong_tail_a_missing_command_and_a_stale_override()
    {
        var dockerfile = Dockerfile();
        const string good = "image: checkedServiceImage\ncommand: [\n  '/usr/bin/env'\n  '-u'\n  'PACKMOAT_DB'\n  'dotnet'\n  '/app/host/Packmoat.Server.dll'\n]\nname: 'PACKMOAT_DB_FILE'\n";
        Assert.Empty(Problems("t", good, dockerfile));
        Assert.NotEmpty(Problems("t", good.Replace("'-u'", "'-x'"), dockerfile));
        Assert.NotEmpty(Problems("t", good.Replace("'dotnet'", "'dotnetx'"), dockerfile));
        Assert.NotEmpty(Problems("t", good + "image: checkedJobImage\n", dockerfile));
        Assert.NotEmpty(Problems("t", good.Replace("PACKMOAT_DB_FILE", "PACKMOAT_OTHER_FILE"), dockerfile));
        Assert.NotEmpty(Problems("t", good, dockerfile.Replace("PACKMOAT_DB=", "PACKMOAT_GONE=")));
        // An image that is not a checked*/var.* form still counts as a container.
        Assert.NotEmpty(Problems("t", good + "image = local.x\nname = 'PACKMOAT_DB_FILE'\n", dockerfile));
        Assert.NotEmpty(Problems("t", good + "image: '${jobImage}'\nname: 'PACKMOAT_DB_FILE'\n", dockerfile));
        // ECS: only a shell running exactly local.secrets_init_script, with no environment, needs no override.
        const string shell = "image = var.a\nentryPoint = [\"/bin/bash\", \"-c\"]\ncommand = [local.secrets_init_script]\n";
        Assert.Empty(Problems("t", good + shell, dockerfile));
        Assert.NotEmpty(Problems("t", good + shell + "environment = [for k, v in local.server_env : { name = k, value = v }]\n", dockerfile));
        Assert.NotEmpty(Problems("t", good + shell.Replace("local.secrets_init_script", "\"exec dotnet x\""), dockerfile));
        Assert.NotEmpty(Problems("t", good + shell.Replace("/bin/bash", "/bin/sh") + "image = var.c\nentryPoint = [\"/bin/sh\", \"-c\"]\ncommand = [\"exec dotnet x\"]\n", dockerfile));
        Assert.NotEmpty(Problems("t", good + "secrets_init_script = <<-EOT\n  exec dotnet x\n  EOT\n", dockerfile));
        Assert.Empty(Problems("t", good + "secrets_init_script = <<-EOT\n  set -e\n  EOT\n", dockerfile));
        Assert.NotEmpty(Problems("t", good + "image = var.b\nentryPoint = [\"dotnet\"]\n", dockerfile));
    }
}
