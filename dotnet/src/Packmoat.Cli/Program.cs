using Packmoat.Admin;

// The core command line: the core's commands with no modules.
return await AdminCli.RunAsync(args, Console.Out, Console.Error, CliEnvironment.FromProcess, []);
