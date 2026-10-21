// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

internal static class ConfigurationUpgradeCommand
{
    internal const string Name = "preserve-config";

    internal static int Run(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = 0;
        try
        {
            if (arguments.Length != ArgumentCount)
                throw new ArgumentException("Expected preserve-config snapshot|restore|complete INSTALL_DIRECTORY BACKUP_DIRECTORY.");
            var backup = new ConfigurationUpgradeBackup(arguments[InstallDirectoryIndex], arguments[BackupDirectoryIndex]);
            switch (arguments[OperationIndex])
            {
                case SnapshotOperation:
                    backup.Snapshot();
                    break;
                case RestoreOperation:
                    backup.Restore();
                    break;
                case CompleteOperation:
                    backup.Complete();
                    break;
                default:
                    throw new ArgumentException("Unknown configuration preservation operation.");
            }
            Console.WriteLine("Configuration preservation operation completed.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Configuration preservation failed: {exception.Message}");
            result = FailureExitCode;
        }
        return result;
    }

    private const int ArgumentCount = 4;
    private const int OperationIndex = 1;
    private const int InstallDirectoryIndex = 2;
    private const int BackupDirectoryIndex = 3;
    private const int FailureExitCode = 1;
    private const string SnapshotOperation = "snapshot";
    private const string RestoreOperation = "restore";
    private const string CompleteOperation = "complete";
}
