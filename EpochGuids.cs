using System;

namespace EpochVisualStudio
{
    /// <summary>
    /// Central place for the GUIDs and command IDs that are shared between the
    /// package code and the <c>EpochCommands.vsct</c> command table. The values
    /// here must stay in sync with the symbols declared in the .vsct file.
    /// </summary>
    internal static class EpochGuids
    {
        public const string PackageGuidString = "1d9ecf8b-2a3e-4c7a-9f1b-7e2d6a4c5b30";
        public const string CmdSetGuidString = "2e0f1a2b-3c4d-5e6f-7a8b-9c0d1e2f3a4b";

        public static readonly Guid CmdSet = new Guid(CmdSetGuidString);
        public static readonly Guid OutputPane = new Guid("3f1a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8");

        public const int CmdSetApiKey = 0x0100;
        public const int CmdSetBaseUrl = 0x0101;
        public const int CmdValidateApiKey = 0x0102;
        public const int CmdOpenDashboard = 0x0103;
        public const int CmdShowOutput = 0x0104;
    }
}
