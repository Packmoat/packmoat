// Checks the conformance vectors outside .NET (#209): every file in ../vectors against its JSON Schema in ../schema (draft-07), with
// Ajv in strict mode. It also makes the checks a schema cannot say (../README.md, section 6): the vector and schema files pair one to
// one, case names are unique in a file, and every time is a real calendar time. Run from this folder: npm ci, then npm run check.
// Exit 0 when every file passes, 1 otherwise. A future implementation's runner can start from here.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const major = Number(process.versions.node.split(".")[0]);
if (major < 20) {
  console.error(`FAIL Node ${process.versions.node} is too old: this check needs Node 20 or later`);
  process.exit(1);
}
let Ajv;
try {
  ({ default: Ajv } = await import("ajv"));
} catch (e) {
  console.error(`FAIL Ajv is not installed: run npm ci in this folder first (${e.code ?? e.message})`);
  process.exit(1);
}

const spec = join(dirname(fileURLToPath(import.meta.url)), "..");
const vectorsRoot = join(spec, "vectors");
const schemaRoot = join(spec, "schema");

/** The groups under a root: each *.json file's path without .json, with / separators, sorted. */
function groups(root) {
  return readdirSync(root, { recursive: true, withFileTypes: true })
    .filter((e) => e.isFile() && e.name.endsWith(".json"))
    .map((e) => relative(root, join(e.parentPath ?? e.path, e.name)).split(sep).join("/").slice(0, -".json".length))
    .sort();
}

const timeFields = new Set(["now", "expiresAt", "publishedAt"]);
const utc = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(\.\d{1,3})?Z$/;

/** True when a time in the spec's form names a real instant (no 30 February, no hour 24). */
function realTime(s) {
  const m = utc.exec(s);
  if (!m) return false;
  const [y, mo, d, h, mi, se] = m.slice(1, 7).map(Number);
  const t = new Date(Date.UTC(y, mo - 1, d, h, mi, se));
  return t.getUTCFullYear() === y && t.getUTCMonth() === mo - 1 && t.getUTCDate() === d
    && t.getUTCHours() === h && t.getUTCMinutes() === mi && t.getUTCSeconds() === se;
}

/** Every time in a case, by its JSON path: the named time fields anywhere, and each value of a publishDates map. */
function* times(node, path) {
  if (Array.isArray(node)) {
    for (const [i, v] of node.entries()) yield* times(v, `${path}[${i}]`);
  } else if (node !== null && typeof node === "object") {
    for (const [k, v] of Object.entries(node)) {
      if (k === "body") continue; // an upstream's JSON: not the vector's own times
      if (timeFields.has(k) && typeof v === "string") yield [`${path}.${k}`, v];
      else if (k === "publishDates" && v !== null && typeof v === "object")
        for (const [ver, t] of Object.entries(v)) yield [`${path}.publishDates[${JSON.stringify(ver)}]`, t];
      else yield* times(v, `${path}.${k}`);
    }
  }
}

const ajv = new Ajv({ strict: true, allErrors: true });
const failures = [];
const vectorGroups = groups(vectorsRoot);
const schemaGroups = groups(schemaRoot);

for (const g of schemaGroups.filter((g) => !vectorGroups.includes(g))) failures.push(`${g}: a schema with no vector file`);
for (const g of vectorGroups) {
  if (!schemaGroups.includes(g)) {
    failures.push(`${g}: a vector file with no schema`);
    continue;
  }
  let doc;
  try {
    doc = JSON.parse(readFileSync(join(vectorsRoot, `${g}.json`), "utf8"));
  } catch (e) {
    failures.push(`${g}: not JSON (${e.message})`);
    continue;
  }
  let validate;
  try {
    validate = ajv.compile(JSON.parse(readFileSync(join(schemaRoot, `${g}.json`), "utf8")));
  } catch (e) {
    failures.push(`${g}: the schema is not a valid JSON Schema (${e.message})`);
    continue;
  }
  if (!validate(doc)) {
    for (const e of validate.errors) failures.push(`${g}: ${e.instancePath || "/"} ${e.message} ${JSON.stringify(e.params)}`);
    continue;
  }
  const seen = new Set();
  for (const c of doc.cases) {
    if (seen.has(c.name)) failures.push(`${g}: the case name "${c.name}" is used twice`);
    seen.add(c.name);
    for (const [path, t] of times(c, "")) if (!realTime(t)) failures.push(`${g}: "${c.name}"${path} is not a real time: ${t}`);
  }
  console.log(`ok   ${g} (${doc.cases.length} cases)`);
}

if (vectorGroups.length === 0) failures.push(`no vector files found in ${vectorsRoot}`);
for (const f of failures) console.error(`FAIL ${f}`);
process.exit(failures.length === 0 ? 0 : 1);
