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
            Console.WriteLine("Проверка и установка пакетов ntfs-3g и chntpw...");
            var (pacmanOutput, pacmanExitCode) = RunCommand("sudo", "pacman -Sy --noconfirm --needed ntfs-3g chntpw");

            if (pacmanExitCode != 0)
            {
                Console.WriteLine($"Ошибка при установке пакетов (код {pacmanExitCode}):\n{pacmanOutput}");
                return;
            }

            string mountPoint = "/mnt";
            
            Console.WriteLine("\nПоиск всех NTFS-разделов...");
            var (lsblkOutput, _) = RunCommand("lsblk", "-r -n -o PATH,FSTYPE,MOUNTPOINT");

            List<string> ntfsPartitions = new List<string>();

            foreach (var line in lsblkOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Берем несмонтированные разделы ntfs
                if (parts.Length == 2 && parts[1].Equals("ntfs", StringComparison.OrdinalIgnoreCase))
                {
                    ntfsPartitions.Add(parts[0]);
                }
            }

            if (ntfsPartitions.Count == 0)
            {
                Console.WriteLine("Свободные NTFS-разделы не найдены.");
                return;
            }

            Console.WriteLine($"Найдено NTFS-разделов для проверки: {ntfsPartitions.Count} ({string.Join(", ", ntfsPartitions)})");

            string? foundWindowsPartition = null;

            foreach (var partition in ntfsPartitions)
            {
                Console.WriteLine($"\n--- Проверка раздела {partition} ---");

                var (roOutput, roExitCode) = RunCommand("sudo", $"mount -o ro {partition} {mountPoint}");

                if (roExitCode != 0)
                {
                    RunCommand("sudo", $"ntfsfix -d {partition}");
                    (roOutput, roExitCode) = RunCommand("sudo", $"mount -o ro {partition} {mountPoint}");

                    if (roExitCode != 0)
                    {
                        Console.WriteLine($"Не удалось открыть {partition}: {roOutput}. Пропускаем...");
                        continue;
                    }
                }

                if (IsWindowsSystemPartition(mountPoint))
                {
                    Console.WriteLine($"[+] Отлично! На разделе {partition} найдена система Windows!");
                    RunCommand("sudo", $"umount -lf {mountPoint}");
                    foundWindowsPartition = partition;
                    break;
                }
                else
                {
                    Console.WriteLine($"[-] На разделе {partition} нет папок Windows/System32. Ищем дальше...");
                    RunCommand("sudo", $"umount -lf {mountPoint}");
                }
            }

            if (foundWindowsPartition == null)
            {
                Console.WriteLine("\nНи на одном из NTFS-разделов не обнаружена установленная Windows.");
                return;
            }

            // Монтируем раздел на запись (сначала чистим флаг грязного тома через ntfsfix -d на случай Быстрого запуска)
            Console.WriteLine($"\nПодготовка и монтирование системного раздела {foundWindowsPartition} в режиме RW...");
            RunCommand("sudo", $"ntfsfix -d {foundWindowsPartition}");

            var (mountOutput, mountExitCode) = RunCommand("sudo", $"mount -o rw {foundWindowsPartition} {mountPoint}");

            if (mountExitCode != 0)
            {
                // Запасной вариант через ntfs-3g с удалением гибернации
                (mountOutput, mountExitCode) = RunCommand("sudo", $"mount -t ntfs-3g -o remove_hiberfile,rw {foundWindowsPartition} {mountPoint}");
                if (mountExitCode != 0)
                {
                    Console.WriteLine($"Ошибка при монтировании (код {mountExitCode}):\n{mountOutput}");
                    return;
                }
            }

            Console.WriteLine($"УСПЕХ! Диск с Windows ({foundWindowsPartition}) смонтирован в {mountPoint}");

            try
            {
                string? samPath = FindSamFilePath(mountPoint);
                if (samPath == null)
                {
                    Console.WriteLine("Ошибка: файл SAM не найден!");
                    return;
                }

                // 1. Получаем список пользователей
                var listResult = RunCommand("sudo", $"chntpw -l \"{samPath}\"");
                var allUsers = ParseUser(listResult.Output);

                var adminUsers = allUsers.Where(u => u.IsAdmin).ToList();

                if (adminUsers.Count == 0)
                {
                    Console.WriteLine("Не найдено пользователей с правами администратора.");
                    Console.WriteLine(listResult.Output);
                    return;
                }

                Console.WriteLine("\nНайдены пользователи с правами администратора:");
                for (int i = 0; i < adminUsers.Count; i++)
                {
                    string status = adminUsers[i].IsLocked ? "[Отключен]" : "[Активен]";
                    Console.WriteLine($"{i + 1}. {adminUsers[i].Username} (RID: {adminUsers[i].RID}) {status}");
                }

                Console.Write("\nВведите номер пользователя, для которого хотите сбросить пароль: ");
                if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > adminUsers.Count)
                {
                    Console.WriteLine("Неверный ввод. Завершение программы.");
                    return;
                }

                SamUser selectedUser = adminUsers[choice - 1];
                Console.WriteLine($"\nОбработка аккаунта: {selectedUser.Username} (RID: 0x{selectedUser.RID})...");

                // 2. Формируем команды для флага -u 0xRID (ровно как в твоем терминале!)
                string commands = "";

                if (selectedUser.IsLocked)
                {
                    Console.WriteLine("-> Аккаунт отключен. Включаем его (команда 2)...");
                    commands += "2\n";
                }

                Console.WriteLine("-> Сбрасываем пароль (команда 1) и сохраняем (q -> y)...");
                commands += "1\nq\ny\n";

                // Важно: запускаем с флагом -u 0xRID (тогда кириллица в имени "Администратор" не мешает!)
                var resetResult = RunCommand("sudo", $"chntpw -u 0x{selectedUser.RID} \"{samPath}\"", commands);

                if (resetResult.Output.Contains("<SAM> - OK") || resetResult.Output.Contains("Password cleared"))
                {
                    Console.WriteLine($"\n[ГОТОВО] Пароль для пользователя {selectedUser.Username} успешно сброшен!");
                }
                else
                {
                    Console.WriteLine($"\nНе удалось сбросить пароль. Вывод chntpw:\n{resetResult.Output}");
                }
            }
            finally
            {
                Console.WriteLine("\nСинхронизация диска и размонтирование...");
                RunCommand("sync", "");
                RunCommand("sudo", $"umount {mountPoint}");
                Console.WriteLine("Диск безопасно размонтирован.");
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