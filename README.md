# AutoUnlockWinUsersFromLinux — Windows Password Reset Tool for Linux (C# `chntpw` Wrapper)

[🇬🇧 English](#-english-version) | [🇷🇺 Русский](#-русская-версия)

---

## 🇬🇧 English Version

A **C# (.NET)** CLI utility for Linux that automatically finds an installed Windows OS across your drives, unlocks disabled accounts, and resets local user passwords using `chntpw`.

### Why I Made This Project
When resetting a forgotten Windows password from a Linux Live USB or dual-boot system, you usually have to manually search for the right partition via `lsblk`, deal with NTFS mounting, find the exact case-sensitive path to the `SAM` registry file, and navigate the `chntpw` interactive prompts (which often corrupt non-ASCII/Cyrillic usernames like `Администратор`).

I built this tool to automate the entire process in a single run:
1. Automatically scans for all unmounted NTFS partitions on the machine.
2. Safely mounts each partition in Read-Only (`ro`) mode and checks for the `Windows`, `Users`, and `Windows/System32/config` folder structure (case-insensitive).
3. Clears the NTFS dirty flag via `ntfsfix -d` and mounts the found Windows partition in Read-Write (`rw`) mode.
4. Parses all user accounts from the `SAM` hive (both administrators and regular users) and displays a clean numbered list.
5. Interacts with the selected account directly via its hexadecimal **RID** (`-u 0xRID`), completely avoiding encoding bugs with non-English characters or spaces in usernames.
6. Checks if the chosen account is disabled or locked (`dis/lock`): if locked, it automatically unlocks it (command `2`), clears the password (command `1`), saves the hive changes, and safely unmounts the drive via `sync` and `umount`.

### What It Uses
* **C# / .NET** (`System.Diagnostics.Process`, `Regex`) — process execution, `stdin`/`stdout` stream redirection, and parsing user tables.
* **`pacman`** — automatically checks and installs `ntfs-3g` and `chntpw` at startup.
* **`lsblk`** — detects unmounted NTFS block devices.
* **`mount` / `umount` / `ntfsfix` / `ntfs-3g`** — handles NTFS partition mounting.
* **`chntpw`** — lists users and edits the `Windows/System32/config/SAM` registry hive.

### Author's Notes & Current Limitations
* **Windows Hibernation / Fast Startup (Error 127):** Currently, mounting a Windows partition that contains an active hibernation file (`hiberfil.sys`) **does not work** and fails with **exit code 127**. Make sure Windows was fully shut down (e.g., by holding `Shift` while clicking Shut Down, or with Fast Startup disabled) before running this tool.
* **Arch-based distros only (Arch, CachyOS, Manjaro, etc.) for now:** At startup, the code runs `sudo pacman -Sy --noconfirm --needed ntfs-3g chntpw`. On distros with `apt` (Debian/Ubuntu) or `dnf` (Fedora), it will fail at the package check step unless you edit that line.
* **Mounts directly to `/mnt`:** Currently, the script uses `/mnt` as the mount point and runs `umount -lf /mnt` during partition checks. Make sure you don't have other drives mounted directly at `/mnt` before running it.
* **Windows partition must be unmounted first:** The scanner only picks up NTFS partitions that do not have an active mount point in `lsblk`. If you already opened the Windows drive in your file manager (like Dolphin), unmount it first.
* **Local accounts only:** Works with local Windows accounts stored in the `SAM` file[cite: 3]. It does not reset cloud-based Microsoft account passwords.

### How to Run
```bash
sudo ./chntpw
```



# AutoUnlockWinUsersFromLinux — Сброс пароля Windows из Linux (C# обертка над `chntpw`)

# 🇷🇺 Русская Версия

Консольная утилита на **C# (.NET)** для Linux, которая автоматически находит установленную систему Windows на дисках компьютера, разблокирует учетные записи и сбрасывает пароли пользователей через `chntpw`[cite: 3].

## Для чего сделан этот проект
Когда нужно сбросить забытый пароль Windows с Live-флешки или второй системы Linux, приходится вручную искать нужный раздел через `lsblk`[cite: 3], разбираться с монтированием NTFS[cite: 3], искать путь к файлу `SAM` и вручную прокликивать меню `chntpw`, где русские имена пользователей (например, `Администратор`) превращаются в нечитаемые символы[cite: 1, 3].

Я написал эту программу, чтобы автоматизировать весь процесс в один запуск:
1. Программа сама сканирует все несмонтированные NTFS-разделы[cite: 3].
2. По очереди монтирует их в безопасном режиме чтения (`ro`) и проверяет наличие папок `Windows`, `Users` и `Windows/System32/config` без учета регистра букв[cite: 3].
3. Очищает флаг «грязного» тома через `ntfsfix -d` и монтирует найденный раздел с Windows в режиме чтения и записи (`rw`)[cite: 3].
4. Парсит таблицу всех пользователей из файла реестра `SAM` (как администраторов, так и обычных юзеров) и выводит удобный нумерованный список[cite: 3].
5. Обращается к выбранному пользователю напрямую по его **RID** (`-u 0xRID`), благодаря чему кириллица или пробелы в имени аккаунта ничего не ломают[cite: 3].
6. Автоматически проверяет, отключен ли аккаунт (`dis/lock`): если он заблокирован — сама включает его (команда `2`), затем сбрасывает пароль (команда `1`), сохраняет изменения в `SAM` и безопасно размонтирует диск через `sync` и `umount`[cite: 3].

## Что используется в проекте
* **C# / .NET** (`System.Diagnostics.Process`, `Regex`) — запуск системных утилит, перенаправление потоков ввода/вывода (`stdin`/`stdout`) и парсинг таблицы пользователей[cite: 3].
* **`pacman`** — автоматическая установка пакетов `ntfs-3g` и `chntpw` при старте[cite: 3].
* **`lsblk`** — поиск разделов с файловой системой NTFS[cite: 3].
* **`mount` / `umount` / `ntfsfix` / `ntfs-3g`** — работа с разделами NTFS[cite: 3].
* **`chntpw`** — чтение списка учетных записей и редактирование файла `Windows/System32/config/SAM`[cite: 3].

## Замечания от меня (на данный момент)
* **Гибернация / Быстрый запуск Windows (ошибка 127):** На данный момент спокойное монтирование диска, если на нём остался файл гибернации (`hiberfil.sys`), **не работает** — при попытке такого монтирования вылетает **ошибка с кодом 127**. Перед использованием утилиты Windows должна быть выключена полностью (например, через зажатый `Shift` при нажатии «Завершение работы» или с отключенным Fast Startup).
* **Только для Arch-подобных дистрибутивов (Arch, CachyOS, Manjaro и др.):** Сейчас в начале кода жестко прописан вызов `sudo pacman -Sy --noconfirm --needed ntfs-3g chntpw`[cite: 3]. На системах с `apt` (Debian/Ubuntu) или `dnf` (Fedora) программа выдаст ошибку на этапе проверки пакетов.
* **Монтирование напрямую в `/mnt`:** В текущей версии раздел монтируется прямо в папку `/mnt`, а при проверке вызывается `umount -lf /mnt`[cite: 3]. Перед запуском убедитесь, что у вас в `/mnt` не примонтировано ничего важного.
* **Раздел с Windows должен быть размонтирован:** Программа ищет только те разделы NTFS, у которых нет активной точки монтирования в `lsblk`[cite: 3]. Если вы до этого открыли диск с Windows в проводнике (например, в Dolphin), размонтируйте его перед запуском.
* **Только локальные аккаунты:** Утилита сбрасывает пароли локальных учетных записей в файле `SAM`[cite: 3]. Пароли от онлайн-аккаунтов Microsoft она не сбрасывает.

## Как запустить
```bash
sudo ./chntpw
```
