using System.ComponentModel;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal sealed partial class MainWindow
{
    private bool updateBusy;

    private async Task CheckForUpdate()
    {
        if (Diagnostic) { message = "Preview only · update checks are disabled."; return; }
        if (updateBusy) return;
        updateBusy = true;
        Caption(Update, "Checking…");
        Refresh();
        try
        {
            using var client = ReleaseUpdate.CreateClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await ReleaseUpdate.LatestAsync(client, deadline.Token);
            if (closed) return;
            if (release.Version <= UpdateInstaller.CurrentVersion)
            {
                message = $"Glide v{UpdateInstaller.VersionText} is up to date.";
                return;
            }
            if (uiBusy || confirmation is not null || pairingServer?.IsPairing == true)
                throw new IOException("Finish or cancel pairing before installing an update.");
            string targets = service is null ? Environment.ProcessPath! : ServiceHost.Executable;
            if (service is null && File.Exists(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid"))) targets += "\n" + ServiceHost.Executable;
            if (MessageBox(window, $"Glide {release.Tag} is available.\n\nUpdate:\n{targets}\n\nThe download is verified before sharing stops. Settings and pairing are kept. Approve the administrator prompt as this Windows account.\n\nDownload and install now?", "Glide update", 0x24) != 6) return;
            // Save before launching. A settings failure leaves the session running.
            Remember(); SaveSettings();
            string eventName = UpdateInstaller.EventPrefix + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName, out bool created);
            if (!created) throw new IOException("An update handoff event already exists.");
            await LaunchUpdate(release.Tag, eventName, ready);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { message = "Update canceled at the administrator prompt. Sharing continues."; }
        catch (OperationCanceledException) when (closed) { }
        catch (OperationCanceledException) { message = "Update check timed out. Try again when GitHub is reachable."; }
        catch (Exception ex) { message = "Update: " + ex.Message; }
        finally
        {
            updateBusy = false;
            if (!closed) { Caption(Update, "Check for updates"); Refresh(); }
        }
    }

    private async Task LaunchUpdate(string tag, string eventName, EventWaitHandle ready)
    {
        using var helper = UpdateInstaller.Launch(tag, eventName);
        Caption(Update, "Downloading…");
        message = "The administrator helper is verifying the release. Sharing continues until it is ready.";
        Refresh();
        while (!helper.HasExited)
        {
            if (ready.WaitOne(0))
            {
                StopSharing("Updating Glide · local control");
                // Retain a Pause/emergency-stop change made during the download.
                Remember(); SaveSettings();
                DestroyWindow(window);
                return;
            }
            await Task.Delay(150, windowLifetime.Token);
        }
        message = "The update helper closed. See its completion/error dialog; sharing has not been stopped here.";
    }
}
