using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Advanced_Combat_Tracker;

namespace ACT_DiscordTriggers {
  // Resolves the plugin's managed closure from the libs/ folder next to the bootstrap
  // DLL, loading each assembly from BYTES (Assembly.Load(byte[])) so the files on disk
  // are never locked — that is what lets the auto-updater overwrite libs/ in place while
  // ACT is running. Matching is by SIMPLE name (version-agnostic), so a single shipped
  // copy satisfies any requested version and our closure stays isolated from whatever
  // other ACT plugins loaded into the shared AppDomain.
  internal sealed class AssemblyResolver {
    private readonly string[] probeDirs;
    private readonly Dictionary<string, Assembly> cache =
      new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private bool hooked;

    public AssemblyResolver(string pluginDir, string appDataPluginDir) {
      // libs/ first (the normal install), then the plugin root (loose-file fallback),
      // then ACT's AppData plugin dir + its libs/ (alternate install location).
      var dirs = new List<string>();
      if (!string.IsNullOrEmpty(pluginDir)) {
        dirs.Add(Path.Combine(pluginDir, "libs"));
        dirs.Add(pluginDir);
      }
      if (!string.IsNullOrEmpty(appDataPluginDir)) {
        dirs.Add(Path.Combine(appDataPluginDir, "libs"));
        dirs.Add(appDataPluginDir);
      }
      probeDirs = dirs.ToArray();
    }

    public void Hook() {
      if (hooked) return;
      AppDomain.CurrentDomain.AssemblyResolve += Resolve;
      hooked = true;
    }

    public void Unhook() {
      if (!hooked) return;
      AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
      hooked = false;
    }

    // Explicitly resolve the named assembly (e.g. the Main assembly) through the same
    // byte-load path, so the bootstrap never holds a file lock on it either.
    public Assembly Load(string simpleName) {
      var asm = Resolve(null, new ResolveEventArgs(simpleName));
      if (asm == null)
        throw new FileNotFoundException($"Could not locate {simpleName}.dll in the plugin's libs/ folder.");
      return asm;
    }

    private Assembly Resolve(object sender, ResolveEventArgs args) {
      string name;
      try { name = new AssemblyName(args.Name).Name; } catch { return null; }
      // Resource satellite probes (e.g. "X.resources") are not part of our closure.
      if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

      lock (cache) {
        if (cache.TryGetValue(name, out var cached)) return cached;
        foreach (var dir in probeDirs) {
          string path;
          try { path = Path.Combine(dir, name + ".dll"); } catch { continue; }
          if (!File.Exists(path)) continue;
          try {
            // Load from bytes: no file handle is retained, so the DLL stays overwritable.
            var asm = Assembly.Load(File.ReadAllBytes(path));
            cache[name] = asm;
            return asm;
          } catch (Exception ex) {
            // A corrupt/half-written libs/ DLL (e.g. an interrupted auto-update) must not
            // throw out of AssemblyResolve — log a breadcrumb and fall through to the next
            // probe dir, else return null so the CLR reports the load failure normally.
            try { ActGlobals.oFormActMain?.WriteExceptionLog(ex, $"ACT_DiscordTriggers: failed to byte-load {name} from {path}."); } catch { }
          }
        }
      }
      return null;
    }
  }
}
