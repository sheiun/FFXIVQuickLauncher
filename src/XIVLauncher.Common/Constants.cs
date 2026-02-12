using System;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common
{
    public static class Constants
    {
        public const string BASE_GAME_VERSION = "2012.01.01.0000.0000";

        public const uint STEAM_APP_ID = 39210;
        public const uint STEAM_FT_APP_ID = 312060;

        // Taiwan server URLs
        public const string TW_PATCH_GAMEVER_URL = "http://patch-gamever.ffxiv.com.tw/http/win32/ffxivtc_release_tc_game/{0}/";
        public const string TW_PATCH_LIST_URL = "https://user-cdn.ffxiv.com.tw/launcher/patch/v2.txt";
        public const string TW_LOBBY_HOST = "neolobby01.ffxiv.com.tw";
        public const int TW_LOBBY_PORT = 54994;
        public const string TW_GM_SERVER_HOST = "frontier.ffxiv.com.tw";
        public const string TW_SAVE_DATA_BANK_HOST = "config-dl.ffxiv.com.tw";

        // Dalamud (yanmucorp) URLs
        public const string TW_DALAMUD_RELEASE_URL = "https://api.github.com/repos/yanmucorp/Dalamud/releases/latest";
        public const string TW_DALAMUD_ASSET_URL = "https://raw.githubusercontent.com/yanmucorp/DalamudAssets/master/assetCN.json";
        public const string TW_DALAMUD_VERSION_INFO_URL = "https://aonyx.ffxiv.wang/Dalamud/Release/VersionInfo?track=release";

        // NuGet runtime sources for Dalamud
        public const string NUGET_BASE_URL = "https://api.nuget.org/v3-flatcontainer";
        public const string NUGET_MIRROR_URL = "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote";
        public const string DOTNET_RUNTIME_VERSION = "9.0.11";

        public static string PatcherUserAgent => GetPatcherUserAgent(PlatformHelpers.GetPlatform());

        private static string GetPatcherUserAgent(Platform platform)
        {
            switch (platform)
            {
                case Platform.Win32:
                case Platform.Win32OnLinux:
                case Platform.Linux:
                    return "FFXIV PATCH CLIENT";

                case Platform.Mac:
                    return "FFXIV-MAC PATCH CLIENT";

                default:
                    throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
            }
        }
    }
}