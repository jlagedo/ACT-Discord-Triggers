using System;
using System.Threading;
using System.Threading.Tasks;

namespace ACT_DiscordTriggers.Core.Update {
  // The slice of the auto-updater the ViewModel drives. The production implementation lives
  // in the Main assembly (it needs ACT's RestartACT, a confirm dialog, and the bridge
  // shutdown); the VM depends only on this interface so it stays ACT-free and testable.
  // Throttling + LastUpdateCheck persistence are the VM's job (it owns PluginSettings); this
  // just performs the network check and the apply.
  public interface IUpdateService {
    // Query GitHub for the latest release and compare to the running version. Throws on a
    // network/parse failure so the VM can report it; UpdateInfo.IsNewer says whether to act.
    Task<UpdateInfo> CheckAsync(CancellationToken ct = default);

    // Confirm with the user, download, stop the bridge, swap files, and restart ACT.
    // Returns false if the user declines or the apply fails (no restart in that case).
    // status receives human-readable progress lines for the UI/log.
    Task<bool> ApplyAsync(UpdateInfo info, IProgress<string> status = null, CancellationToken ct = default);
  }
}
