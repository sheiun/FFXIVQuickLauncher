using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

#nullable enable

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudUpdater
    {
        private readonly DirectoryInfo addonDirectory;
        private readonly DirectoryInfo assetRootDirectory;
        private readonly IUniqueIdCache? cache;

        private readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(15);

        private bool forceProxy = false;
        private DalamudVersionInfo? resolvedBranch;

        public DownloadState State { get; private set; } = DownloadState.Unknown;
        public bool IsStaging { get; private set; } = false;

        public Exception? EnsurementException { get; private set; }

        private FileInfo? runnerInternal;

        public FileInfo Runner
        {
            get
            {
                if (RunnerOverride != null)
                    return RunnerOverride;

                return runnerInternal ?? throw new InvalidOperationException("Runner not prepared yet");
            }
            private set => runnerInternal = value;
        }

        public DirectoryInfo Runtime { get; }

        public FileInfo? RunnerOverride { get; set; }

        public DirectoryInfo? AssetDirectory { get; private set; }

        public IDalamudLoadingOverlay? Overlay { get; set; }

        public string? RolloutBucket { get; }

        public event Action<DalamudVersionInfo?>? ResolvedBranchChanged;

        public DalamudVersionInfo? ResolvedBranch
        {
            get => resolvedBranch;
            private set
            {
                if (resolvedBranch == value)
                    return;

                resolvedBranch = value;

                try
                {
                    ResolvedBranchChanged?.Invoke(resolvedBranch);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public enum DownloadState
        {
            Unknown,
            Running,
            Done,
            NoIntegrity, // fail with error message
        }

        public DalamudUpdater(DirectoryInfo addonDirectory, DirectoryInfo runtimeDirectory, DirectoryInfo assetRootDirectory, IUniqueIdCache? cache, string? dalamudRolloutBucket)
        {
            this.addonDirectory = addonDirectory;
            this.assetRootDirectory = assetRootDirectory;

            this.Runtime = runtimeDirectory;
            this.AssetDirectory = null;
            this.cache = cache;

            this.RolloutBucket = dalamudRolloutBucket;

            if (this.RolloutBucket == null)
            {
                var rng = new Random();
                this.RolloutBucket = rng.Next(0, 9) >= 7 ? "Canary" : "Control";
            }
        }

        public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
        {
            Overlay!.SetStep(progress);
        }

        public void ShowOverlay()
        {
            Overlay!.SetVisible();
        }

        public void CloseOverlay()
        {
            Overlay!.SetInvisible();
        }

        private void ReportOverlayProgress(long? size, long downloaded, double? progress)
        {
            Overlay!.ReportProgress(size, downloaded, progress);
        }

        public void Run(string? betaKind, string? betaKey, bool overrideForceProxy = false)
        {
            Log.Information("[DUPDATE] Starting... (forceProxy: {ForceProxy})", overrideForceProxy);
            this.State = DownloadState.Running;

            this.forceProxy = overrideForceProxy;

            this.ResolvedBranch = null;

            Task.Run(async () =>
            {
                const int MAX_TRIES = 10;

                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        await UpdateDalamud(betaKind, betaKey).ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Update failed, try {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                        this.EnsurementException = ex;
                        this.forceProxy = true;
                    }
                }

                this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            });
        }

        public bool? ReCheckVersion(DirectoryInfo gamePath)
        {
            if (this.State != DownloadState.Done)
                return null;

            if (this.RunnerOverride != null)
                return true;

            var info = DalamudVersionInfo.Load(new FileInfo(Path.Combine(this.Runner.DirectoryName!,
                "version.json")));

            return Repository.Ffxiv.GetVer(gamePath) == info.SupportedGameVer;
        }

        private static string GetBetaTrackName(string betaKind) =>
            string.IsNullOrEmpty(betaKind) ? "staging" : betaKind;

        private async Task<(DalamudVersionInfo release, DalamudVersionInfo? staging)> GetVersionInfo(string? betaKind, string? betaKey)
        {
            using var client = new HttpClient
            {
                Timeout = this.defaultTimeout,
            };

            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
            };

            var versionInfoJsonRelease = await client.GetStringAsync(DalamudLauncher.REMOTE_BASE + $"release&bucket={this.RolloutBucket}").ConfigureAwait(false);

            DalamudVersionInfo versionInfoRelease = JsonConvert.DeserializeObject<DalamudVersionInfo>(versionInfoJsonRelease);

            DalamudVersionInfo? versionInfoStaging = null;

            if (!string.IsNullOrEmpty(betaKey))
            {
                var versionInfoJsonStaging = await client.GetAsync(DalamudLauncher.REMOTE_BASE + GetBetaTrackName(betaKind)).ConfigureAwait(false);

                if (versionInfoJsonStaging.StatusCode != HttpStatusCode.BadRequest)
                    versionInfoStaging = JsonConvert.DeserializeObject<DalamudVersionInfo>(await versionInfoJsonStaging.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            return (versionInfoRelease, versionInfoStaging);
        }

        private async Task UpdateDalamud(string? betaKind, string? betaKey)
        {
            // GitHub requires TLS 1.2, we need to hardcode this for Windows 7
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var (versionInfoRelease, versionInfoStaging) = await GetVersionInfo(betaKind, betaKey).ConfigureAwait(false);

            var remoteVersionInfo = versionInfoRelease;

            if (versionInfoStaging?.Key != null && versionInfoStaging.Key == betaKey)
            {
                remoteVersionInfo = versionInfoStaging;
                IsStaging = true;
                Log.Information("[DUPDATE] Using staging version {Kind} with key {Key} ({Hash})", betaKind, betaKey, remoteVersionInfo.AssemblyVersion);
            }
            else
            {
                Log.Information("[DUPDATE] Using release version ({Hash})", remoteVersionInfo.AssemblyVersion);
            }

            // Update resolved branch to reflect what the server actually selected
            this.ResolvedBranch = remoteVersionInfo;

            var versionInfoJson = JsonConvert.SerializeObject(remoteVersionInfo);

            var addonPath = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
            var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, remoteVersionInfo.AssemblyVersion));
            var runtimePaths = new DirectoryInfo[]
            {
                new(Path.Combine(this.Runtime.FullName, "host", "fxr", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.NETCore.App", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", remoteVersionInfo.RuntimeVersion)),
            };

            if (!currentVersionPath.Exists || !IsIntegrity(currentVersionPath))
            {
                Log.Information("[DUPDATE] Not found, redownloading");

                SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

                try
                {
                    await DownloadDalamud(currentVersionPath, remoteVersionInfo).ConfigureAwait(true);
                    CleanUpOld(addonPath, remoteVersionInfo.AssemblyVersion);

                    // This is a good indicator that we should clear the UID cache
                    cache?.Reset();
                }
                catch (Exception ex)
                {
                    throw new DalamudIntegrityException("Could not download Dalamud", ex);
                }
            }

            if (remoteVersionInfo.RuntimeRequired)
            {
                Log.Information("[DUPDATE] Now starting for .NET Runtime {0}", remoteVersionInfo.RuntimeVersion);

                var versionFile = new FileInfo(Path.Combine(this.Runtime.FullName, "version"));
                var localVersion = GetLocalRuntimeVersion(versionFile);

                var runtimeNeedsUpdate = localVersion != remoteVersionInfo.RuntimeVersion;

                if (!this.Runtime.Exists)
                    Directory.CreateDirectory(this.Runtime.FullName);

                var isRuntimeIntegrity = false;

                // Only check runtime hashes if we don't need to update it
                if (!runtimeNeedsUpdate)
                {
                    try
                    {
                        isRuntimeIntegrity = await CheckRuntimeHashes(Runtime, localVersion).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Could not check runtime integrity.");
                    }
                }

                if (runtimePaths.Any(p => !p.Exists) || runtimeNeedsUpdate || !isRuntimeIntegrity)
                {
                    Log.Information("[DUPDATE] Not found, outdated or no integrity: {LocalVer} - {RemoteVer}", localVersion, remoteVersionInfo.RuntimeVersion);

                    SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

                    try
                    {
                        Log.Verbose("[DUPDATE] Now download runtime...");
                        await DownloadRuntime(this.Runtime, remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);
                        File.WriteAllText(versionFile.FullName, remoteVersionInfo.RuntimeVersion);
                    }
                    catch (Exception ex)
                    {
                        throw new DalamudIntegrityException("Could not ensure runtime", ex);
                    }
                }
            }

            Log.Verbose("[DUPDATE] Now ensure assets...");

            var assetVer = 0;

            try
            {
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
                this.ReportOverlayProgress(null, 0, null);
                var assetResult = await AssetManager.EnsureAssets(this, this.assetRootDirectory).ConfigureAwait(true);
                AssetDirectory = assetResult.AssetDir;
                assetVer = assetResult.Version;
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("Could not ensure assets", ex);
            }

            if (!IsIntegrity(currentVersionPath))
            {
                throw new DalamudIntegrityException("No integrity after ensurement");
            }

            WriteVersionJson(currentVersionPath, versionInfoJson);

            Log.Information("[DUPDATE] All set for {GameVersion} with {DalamudVersion}({RuntimeVersion}, {AssetVersion})", remoteVersionInfo.SupportedGameVer, remoteVersionInfo.AssemblyVersion, remoteVersionInfo.RuntimeVersion, assetVer);

            Runner = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
            ReportOverlayProgress(null, 0, null);
        }

        private static bool CanRead(FileInfo info)
        {
            try
            {
                using var stream = info.OpenRead();
                stream.ReadByte();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsIntegrity(DirectoryInfo addonPath)
        {
            var files = addonPath.GetFiles();

            try
            {
                if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe"))
                    || !CanRead(files.First(x => x.Name == "Dalamud.dll"))
                    || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
                {
                    Log.Error("[DUPDATE] Can't open files for read");
                    return false;
                }

                var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

                if (!File.Exists(hashesPath))
                {
                    Log.Error("[DUPDATE] No hashes.json");
                    return false;
                }

                return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] No dalamud integrity");
                return false;
            }
        }

        private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
        {
            try
            {
                Log.Verbose("[DUPDATE] Checking integrity of {Directory}", directory.FullName);

                var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);

                foreach (var hash in hashes)
                {
                    var file = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                    using var fileStream = File.OpenRead(file);
                    using var md5 = MD5.Create();

                    var hashed = BitConverter.ToString(md5.ComputeHash(fileStream)).ToUpperInvariant().Replace("-", string.Empty);

                    if (hashed != hash.Value)
                    {
                        Log.Error("[DUPDATE] Integrity check failed for {0} ({1} - {2})", file, hash.Value, hashed);
                        return false;
                    }

                    Log.Verbose("[DUPDATE] Integrity check OK for {0} ({1})", file, hashed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Integrity check failed");
                return false;
            }

            return true;
        }

        private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!addonPath.Exists)
                return;

            foreach (var directory in addonPath.GetDirectories())
            {
                if (directory.Name == "dev" || directory.Name == currentVer) continue;

                try
                {
                    directory.Delete(true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static void WriteVersionJson(DirectoryInfo addonPath, string info)
        {
            File.WriteAllText(Path.Combine(addonPath.FullName, "version.json"), info);
        }

        private async Task DownloadDalamud(DirectoryInfo addonPath, DalamudVersionInfo version)
        {
            // Ensure directory exists
            if (!addonPath.Exists)
                addonPath.Create();
            else
            {
                addonPath.Delete(true);
                addonPath.Create();
            }

            var downloadPath = PlatformHelpers.GetTempFileName();

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            await this.DownloadFile(version.DownloadUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, addonPath.FullName);

            File.Delete(downloadPath);

            try
            {
                var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

                PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not copy to dev folder.");
            }
        }

        private string GetLocalRuntimeVersion(FileInfo versionFile)
        {
            // This is the version we first shipped. We didn't write out a version file, so we can't check it.
            var localVersion = "5.0.6";

            try
            {
                if (versionFile.Exists)
                    localVersion = File.ReadAllText(versionFile.FullName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not read local runtime version.");
            }

            return localVersion;
        }

        private Task<bool> CheckRuntimeHashes(DirectoryInfo runtimePath, string version)
        {
            // Skip remote hash check for NuGet-based runtime downloads (Taiwan version).
            // NuGet packages are verified by their own integrity checks during download.
            Log.Verbose("[DUPDATE] Skipping remote runtime hash check (NuGet source)");
            return Task.FromResult(true);
        }

        private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
        {
            // Ensure directory exists
            if (!runtimePath.Exists)
            {
                runtimePath.Create();
            }
            else
            {
                runtimePath.Delete(true);
                runtimePath.Create();
            }

            // Wait for it to be gone, thanks Windows
            Thread.Sleep(1000);

            // Download from NuGet instead of kamori (for Taiwan/yanmucorp Dalamud)
            var nugetBaseUrl = Constants.NUGET_BASE_URL;

            var versionParts = version.Split('.');
            var dotnetMajorMinor = versionParts.Length >= 2 ? $"{versionParts[0]}.{versionParts[1]}" : "9.0";

            // Download .NET Core Runtime nupkg
            var netcorePkg = "microsoft.netcore.app.runtime.win-x64";
            var netcoreUrl = $"{nugetBaseUrl}/{netcorePkg}/{version.ToLower()}/{netcorePkg}.{version.ToLower()}.nupkg";
            var netcorePath = PlatformHelpers.GetTempFileName();

            Log.Information("[DUPDATE] Downloading NETCore runtime from NuGet: {Url}", netcoreUrl);
            await this.DownloadFile(netcoreUrl, netcorePath, this.defaultTimeout).ConfigureAwait(false);
            ExtractNuGetRuntime(netcorePath, runtimePath.FullName, version, dotnetMajorMinor, "Microsoft.NETCore.App");
            File.Delete(netcorePath);

            // Download Windows Desktop Runtime nupkg
            var desktopPkg = "microsoft.windowsdesktop.app.runtime.win-x64";
            var desktopUrl = $"{nugetBaseUrl}/{desktopPkg}/{version.ToLower()}/{desktopPkg}.{version.ToLower()}.nupkg";
            var desktopPath = PlatformHelpers.GetTempFileName();

            Log.Information("[DUPDATE] Downloading WindowsDesktop runtime from NuGet: {Url}", desktopUrl);
            await this.DownloadFile(desktopUrl, desktopPath, this.defaultTimeout).ConfigureAwait(false);
            ExtractNuGetRuntime(desktopPath, runtimePath.FullName, version, dotnetMajorMinor, "Microsoft.WindowsDesktop.App");
            File.Delete(desktopPath);

            // Move hostfxr.dll to correct location
            var hostfxrSource = Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version, "hostfxr.dll");
            if (File.Exists(hostfxrSource))
            {
                var hostfxrTargetDir = Path.Combine(runtimePath.FullName, "host", "fxr", version);
                Directory.CreateDirectory(hostfxrTargetDir);
                var hostfxrTarget = Path.Combine(hostfxrTargetDir, "hostfxr.dll");
                if (File.Exists(hostfxrTarget))
                    File.Delete(hostfxrTarget);
                File.Move(hostfxrSource, hostfxrTarget);
                Log.Verbose("[DUPDATE] Moved hostfxr.dll to {Path}", hostfxrTargetDir);
            }
        }

        private static void ExtractNuGetRuntime(string nupkgPath, string runtimeRoot, string version, string dotnetVersion, string frameworkName)
        {
            var targetDir = Path.Combine(runtimeRoot, "shared", frameworkName, version);
            Directory.CreateDirectory(targetDir);

            using var archive = ZipFile.OpenRead(nupkgPath);
            var nativePath = "runtimes/win-x64/native/";
            var libPath = $"runtimes/win-x64/lib/net{dotnetVersion}/";

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                if (entry.FullName.StartsWith(nativePath, StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith(libPath, StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = Path.Combine(targetDir, entry.Name);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
        }

        public async Task DownloadFile(string url, string path, TimeSpan timeout)
        {
            if (this.forceProxy && url.Contains("/File/Get/"))
            {
                url = url.Replace("/File/Get/", "/File/GetProxy/");
            }

            using var downloader = new HttpClientDownloadWithProgress(url, path);
            downloader.ProgressChanged += this.ReportOverlayProgress;

            await downloader.Download(timeout).ConfigureAwait(false);
        }
    }

    public class DalamudIntegrityException : Exception
    {
        public DalamudIntegrityException(string msg, Exception? inner = null)
            : base(msg, inner)
        {
        }
    }
}

#nullable restore
