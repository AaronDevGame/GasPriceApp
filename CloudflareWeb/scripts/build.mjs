import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

if (Number(process.versions.node.split(".")[0]) < 22) {
  console.error(`Web export requires Node.js 22 or newer (running ${process.version}). Use Node 24 LTS.`);
  process.exit(1);
}

// Resolve from this file so Wrangler always exports the frontend, not its own folder.
const result = spawnSync(process.execPath,
  ["node_modules/expo/bin/cli", "export", "--platform", "web"],
  { cwd: fileURLToPath(new URL("../../GasPriceClient/", import.meta.url)), stdio: "inherit" });
if (result.error) console.error("Could not start Expo export. Install GasPriceClient dependencies first.");
process.exit(result.status ?? 1);
