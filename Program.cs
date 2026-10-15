using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Program
{
    class Program
    {
        class SamUser
        {
            public string RID { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public bool IsAdmin { get; set; }
            public bool IsLocked { get; set; }
        }

        static void Main()
        {
            Console.WriteLine("Checking and installing ntfs-3g and chntpw packages...");
            var (pacmanOutput, pacmanExitCode) = RunCommand("sudo", "pacman -Sy --noconfirm --needed ntfs-3g chntpw");

            if (pacmanExitCode != 0)
            {
                Console.WriteLine($"Error installing packages (exit code {pacmanExitCode}):\n{pacmanOutput}");
                return;
            }

            string mountPoint = "/mnt";

            Console.WriteLine("\nSearching for all unmounted NTFS partitions...");
            var (lsblkOutput, _) = RunCommand("lsblk", "-r -n -o PATH,FSTYPE,MOUNTPOINT");

            List<string> ntfsPartitions = new List<string>();

            foreach (var line in lsblkOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Pick only unmounted NTFS partitions
                if (parts.Length == 2 && parts[1].Equals("ntfs", StringComparison.OrdinalIgnoreCase))
                {
                    ntfsPartitions.Add(parts[0]);
                }
            }

            if (ntfsPartitions.Count == 0)
            {
                Console.WriteLine("No unmounted NTFS partitions found.");
                return;
            }

            Console.WriteLine($"Found NTFS partitions to check: {ntfsPartitions.Count} ({string.Join(", ", ntfsPartitions)})");

            string? foundWindowsPartition = null;

            foreach (var partition in ntfsPartitions)
            {
                Console.WriteLine($"\n--- Checking partition {partition} ---");

                var (roOutput, roExitCode) = RunCommand("sudo", $"mount -o ro {partition} {mountPoint}");

                if (roExitCode != 0)
                {
                    RunCommand("sudo", $"ntfsfix -d {partition}");
                    (roOutput, roExitCode) = RunCommand("sudo", $"mount -o ro {partition} {mountPoint}");

                    if (roExitCode != 0)
                    {
                        Console.WriteLine($"Failed to open {partition}: {roOutput}. Skipping...");
                        continue;
                    }
                }

                if (IsWindowsSystemPartition(mountPoint))
                {
                    Console.WriteLine($"[+] Great! Windows installation found on {partition}!");
                    RunCommand("sudo", $"umount -lf {mountPoint}");
                    foundWindowsPartition = partition;
                    break;
                }
                else
                {
                    Console.WriteLine($"[-] No Windows/System32 directories on {partition}. Searching next...");
                    RunCommand("sudo", $"umount -lf {mountPoint}");
                }
            }

            if (foundWindowsPartition == null)
            {
                Console.WriteLine("\nNo Windows installation found on any of the NTFS partitions.");
                return;
            }

            // Mount the partition as read-write (clear the dirty volume flag via ntfsfix -d first in case of Fast Startup)
            Console.WriteLine($"\nPreparing and mounting system partition {foundWindowsPartition} in RW mode...");
            RunCommand("sudo", $"ntfsfix -d {foundWindowsPartition}");

            var (mountOutput, mountExitCode) = RunCommand("sudo", $"mount -o rw {foundWindowsPartition} {mountPoint}");

            if (mountExitCode != 0)
            {
                // Fallback using ntfs-3g with hibernation file removal
                (mountOutput, mountExitCode) = RunCommand("sudo", $"mount -t ntfs-3g -o remove_hiberfile,rw {foundWindowsPartition} {mountPoint}");
                if (mountExitCode != 0)
                {
                    Console.WriteLine($"Error mounting partition (exit code {mountExitCode}):\n{mountOutput}");
                    return;
                }
            }

            Console.WriteLine($"SUCCESS! Windows drive ({foundWindowsPartition}) mounted at {mountPoint}");

            try
            {
                string? samPath = FindSamFilePath(mountPoint);
                if (samPath == null)
                {
                    Console.WriteLine("Error: SAM file not found!");
                    return;
                }

                // 1. Get the list of all users
                var listResult = RunCommand("sudo", $"chntpw -l \"{samPath}\"");
                var allUsers = ParseUser(listResult.Output);

                if (allUsers.Count == 0)
                {
                    Console.WriteLine("No users found in the SAM file.");
                    Console.WriteLine(listResult.Output);
                    return;
                }

                Console.WriteLine("\nFound Windows user accounts:");
                for (int i = 0; i < allUsers.Count; i++)
                {
                    string role = allUsers[i].IsAdmin ? "[ADMIN]" : "[USER]";
                    string status = allUsers[i].IsLocked ? "[Disabled/Locked]" : "[Active]";
                    Console.WriteLine($"{i + 1}. {allUsers[i].Username} (RID: {allUsers[i].RID}) {role} {status}");
                }

                Console.Write("\nEnter the number of the user whose password you want to reset: ");
                if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > allUsers.Count)
                {
                    Console.WriteLine("Invalid input. Exiting program.");
                    return;
                }

                SamUser selectedUser = allUsers[choice - 1];
                Console.WriteLine($"\nProcessing account: {selectedUser.Username} (RID: 0x{selectedUser.RID})...");

                // 2. Build commands for the -u 0xRID flag
                string commands = "";

                if (selectedUser.IsLocked)
                {
                    Console.WriteLine("-> Account is disabled/locked. Enabling it (command 2)...");
                    commands += "2\n";
                }

                Console.WriteLine("-> Clearing password (command 1) and saving changes (q -> y)...");
                commands += "1\nq\ny\n";

                // Run with -u 0xRID so non-ASCII characters in usernames don't cause issues
                var resetResult = RunCommand("sudo", $"chntpw -u 0x{selectedUser.RID} \"{samPath}\"", commands);

                if (resetResult.Output.Contains("<SAM> - OK") || resetResult.Output.Contains("Password cleared"))
                {
                    Console.WriteLine($"\n[DONE] Password for user {selectedUser.Username} has been successfully reset!");
                }
                else
                {
                    Console.WriteLine($"\nFailed to reset password. chntpw output:\n{resetResult.Output}");
                }
            }
            finally
            {
                Console.WriteLine("\nSyncing disk and unmounting...");
                RunCommand("sync", "");
                RunCommand("sudo", $"umount {mountPoint}");
                Console.WriteLine("Drive safely unmounted.");
            }
        }

        static List<SamUser> ParseUser(string chntpwOutput)
        {
            var list = new List<SamUser>();
            var regex = new Regex(@"^\|\s*([0-9a-fA-F]+)\s*\|\s*(.+?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|", RegexOptions.Multiline);

            foreach (Match match in regex.Matches(chntpwOutput))
            {
                list.Add(new SamUser
                {
                    RID = match.Groups[1].Value.Trim(),
                    Username = match.Groups[2].Value.Trim(),
                    IsAdmin = match.Groups[3].Value.Contains("ADMIN"),
                    IsLocked = match.Groups[4].Value.Contains("dis/lock")
                });
            }

            return list;
        }

        static string? FindSamFilePath(string rootPath)
        {
            string? winDir = FindDirCaseInsensitive(rootPath, "Windows");
            if (winDir == null) return null;

            string? sys32Dir = FindDirCaseInsensitive(winDir, "System32");
            if (sys32Dir == null) return null;

            string? configDir = FindDirCaseInsensitive(sys32Dir, "config");
            if (configDir == null) return null;

            return Directory.GetFiles(configDir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "SAM", StringComparison.OrdinalIgnoreCase));
        }

        static bool IsWindowsSystemPartition(string rootPath)
        {
            try
            {
                string? winDir = FindDirCaseInsensitive(rootPath, "Windows");
                string? usersDir = FindDirCaseInsensitive(rootPath, "Users");

                if (winDir == null || usersDir == null)
                    return false;

                string? sys32Dir = FindDirCaseInsensitive(winDir, "System32");
                if (sys32Dir == null)
                    return false;

                string? configDir = FindDirCaseInsensitive(sys32Dir, "config");
                return configDir != null;
            }
            catch
            {
                return false;
            }
        }

        static string? FindDirCaseInsensitive(string parentDir, string targetFolderName)
        {
            if (!Directory.Exists(parentDir))
                return null;

            return Directory.GetDirectories(parentDir)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), targetFolderName, StringComparison.OrdinalIgnoreCase));
        }

        static (string Output, int ExitCode) RunCommand(string command, string arguments, string? input = null)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = input != null,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };

                process.Start();

                if (input != null)
                {
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                string resultText = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
                return (resultText.Trim(), process.ExitCode);
            }
            catch (Exception ex)
            {
                return ($"Exception: {ex.Message}", -1);
            }
        }
    }
}