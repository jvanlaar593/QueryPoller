using System;

namespace SsmsQueryPoller
{
    internal static class PackageGuids
    {
        public const string PackageString = "362889d2-5c2e-42be-9ad2-8049a72ac586";
        public const string CmdSetString = "5fa4fc59-4523-4f08-954c-bf0564f42943";
        public const string ToolWindowString = "b9223b1f-cab8-459d-b779-6429c8eac0a6";

        public static readonly Guid CmdSet = new Guid(CmdSetString);
    }

    internal static class PackageIds
    {
        public const int ShowQueryPollerCommandId = 0x0100;
        public const int ExecuteWithPollingCommandId = 0x0200;
    }
}
