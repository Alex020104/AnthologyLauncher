using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Anthology.Releaser.Core;

public sealed class RepackBuildRequest
{
    public string ProjectName { get; init; } = "ANTHOLOGY";

    public string Version { get; init; } = "2.1";

    public string GameSourceRoot { get; init; } = string.Empty;

    public string Mo2SourceRoot { get; init; } = string.Empty;

    public bool IncludeMo2 { get; init; } = true;

    public string OutputRoot { get; init; } = string.Empty;

    public string TemporaryRoot { get; init; } = @"B:\AnthologyReleaserTemp";

    public string SevenZipPath { get; init; } = @"C:\Program Files\7-Zip\7z.exe";

    public string InnoSetupCompilerPath { get; init; } = @"C:\Program Files (x86)\Inno Setup 6\ISCC.exe";

    public string InstallerTemplateRoot { get; init; } = string.Empty;

    public string LauncherFileName { get; init; } = "AnomalyLauncher.exe";

    public string SetupBaseFileName { get; init; } = "Anthology_Setup";

    public string GameArchiveFileName { get; init; } = "Anthology_Game.bin";

    public string Mo2ArchiveFileName { get; init; } = "Anthology_Modpack.bin";

    public bool OverwriteExisting { get; init; }
}

public sealed record RepackBuildResult(
    string SetupPath,
    string GameArchivePath,
    string? Mo2ArchivePath,
    long SourceBytes,
    IReadOnlyList<string> Outputs);

public sealed record RepackToolCommand(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public sealed record RepackToolResult(int ExitCode, string StandardOutput, string StandardError);

public interface IRepackToolRunner
{
    Task<RepackToolResult> RunAsync(
        RepackToolCommand command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class RepackProcessRunner : IRepackToolRunner
{
    public async Task<RepackToolResult> RunAsync(
        RepackToolCommand command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(command.FileName)}.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, standardOutput, progress, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, standardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process completed between HasExited and Kill.
            }
            throw;
        }

        return new RepackToolResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            destination.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                progress?.Report(line.Trim());
            }
        }
    }
}

public sealed class RepackBuilder(IRepackToolRunner? toolRunner = null)
{
    private static readonly HashSet<string> CommonExcludedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".anthology-releaser",
        "$RECYCLE.BIN",
        "System Volume Information",
    };

    private static readonly HashSet<string> GameExcludedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata",
        "logs",
        "screenshots",
        "crashdumps",
        "webcache",
        "AnthologyLauncher",
        "AnomalyLauncher.cfg",
        "commandline.txt",
    };

    private static readonly HashSet<string> Mo2ExcludedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloads",
        "overwrite",
        "logs",
        "crash_dumps",
        "webcache",
        "ModOrganizer.ini",
    };

    private readonly IRepackToolRunner _toolRunner = toolRunner ?? new RepackProcessRunner();

    /// <summary>
    /// Removes abandoned staging directories from an explicitly configured
    /// temporary root. Recent jobs are kept so another releaser process cannot
    /// interrupt an active build.
    /// </summary>
    public static int CleanupStaleJobs(string temporaryRoot, TimeSpan minimumAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.Zero);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot));
        if (!Directory.Exists(root))
        {
            return 0;
        }

        EnsureNotDriveRoot(root, "Временная папка репака");
        var cutoff = DateTime.UtcNow - minimumAge;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "repack-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (!Path.GetDirectoryName(full)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || !new DirectoryInfo(full).Name.StartsWith("repack-", StringComparison.Ordinal))
                {
                    continue;
                }

                var newestWrite = Directory.GetLastWriteTimeUtc(full);
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var write = File.GetLastWriteTimeUtc(file);
                    if (write > newestWrite)
                    {
                        newestWrite = write;
                    }
                    if (newestWrite > cutoff)
                    {
                        break;
                    }
                }
                if (newestWrite > cutoff)
                {
                    continue;
                }

                Directory.Delete(full, recursive: true);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An active process or sync client may still own a file. It will be
                // retried during a later startup without failing the releaser.
            }
        }
        return removed;
    }

    public async Task<RepackBuildResult> BuildAsync(
        RepackBuildRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = ValidateAndResolve(request);
        progress?.Report("Репак: подсчёт файлов и проверка свободного места…");
        var gameBytes = CalculateIncludedBytes(paths.GameSourceRoot, GameExcludedRoots, cancellationToken);
        var mo2Bytes = request.IncludeMo2
            ? CalculateIncludedBytes(paths.Mo2SourceRoot!, Mo2ExcludedRoots, cancellationToken)
            : 0;
        EnsureFreeSpace(paths, gameBytes + mo2Bytes);

        Directory.CreateDirectory(paths.OutputRoot);
        Directory.CreateDirectory(paths.TemporaryRoot);
        var jobRoot = Path.Combine(paths.TemporaryRoot, $"repack-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobRoot);
        var stagedOutputs = new List<(string Source, string Destination)>();
        try
        {
            var stagedGameArchive = Path.Combine(jobRoot, paths.GameArchiveFileName);
            await BuildArchiveAsync(
                paths.SevenZipPath,
                paths.GameSourceRoot,
                stagedGameArchive,
                GameExcludedRoots,
                "игры",
                progress,
                cancellationToken);
            stagedOutputs.Add((stagedGameArchive, Path.Combine(paths.OutputRoot, paths.GameArchiveFileName)));

            string? stagedMo2Archive = null;
            if (request.IncludeMo2)
            {
                stagedMo2Archive = Path.Combine(jobRoot, paths.Mo2ArchiveFileName);
                await BuildArchiveAsync(
                    paths.SevenZipPath,
                    paths.Mo2SourceRoot!,
                    stagedMo2Archive,
                    Mo2ExcludedRoots,
                    "MO2",
                    progress,
                    cancellationToken);
                stagedOutputs.Add((stagedMo2Archive, Path.Combine(paths.OutputRoot, paths.Mo2ArchiveFileName)));
            }

            progress?.Report("Репак: создание Setup.exe…");
            var installerScript = GenerateInstallerScript(
                request,
                paths,
                jobRoot,
                gameBytes,
                mo2Bytes);
            var scriptPath = Path.Combine(jobRoot, "Anthology_Setup.generated.iss");
            await File.WriteAllTextAsync(scriptPath, installerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            var compiler = await _toolRunner.RunAsync(
                new RepackToolCommand(
                    paths.InnoSetupCompilerPath,
                    jobRoot,
                    ["/Qp", scriptPath]),
                progress,
                cancellationToken);
            EnsureSucceeded(compiler, "Inno Setup не смог собрать установщик.");

            var stagedSetup = Path.Combine(jobRoot, paths.SetupBaseFileName + ".exe");
            if (!File.Exists(stagedSetup) || new FileInfo(stagedSetup).Length == 0)
            {
                throw new InvalidDataException("Inno Setup завершился без готового Setup.exe.");
            }
            stagedOutputs.Add((stagedSetup, Path.Combine(paths.OutputRoot, paths.SetupBaseFileName + ".exe")));

            progress?.Report("Репак: атомарная публикация готовых файлов…");
            await CommitOutputsAsync(stagedOutputs, request.OverwriteExisting, cancellationToken);
            var finalOutputs = stagedOutputs.Select(item => item.Destination).ToArray();
            progress?.Report("Репак готов. Временные файлы очищаются…");
            return new RepackBuildResult(
                Path.Combine(paths.OutputRoot, paths.SetupBaseFileName + ".exe"),
                Path.Combine(paths.OutputRoot, paths.GameArchiveFileName),
                request.IncludeMo2 ? Path.Combine(paths.OutputRoot, paths.Mo2ArchiveFileName) : null,
                checked(gameBytes + mo2Bytes),
                finalOutputs);
        }
        finally
        {
            DeleteJobDirectory(jobRoot);
        }
    }

    public static string GenerateInstallerScript(
        RepackBuildRequest request,
        string outputDirectory,
        long gameBytes,
        long mo2Bytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = ValidateAndResolve(request, requireTools: false);
        return GenerateInstallerScript(request, paths, Path.GetFullPath(outputDirectory), gameBytes, mo2Bytes);
    }

    private async Task BuildArchiveAsync(
        string sevenZipPath,
        string sourceRoot,
        string archivePath,
        HashSet<string> excludedRoots,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(sourceRoot);
        var parent = source.Parent?.FullName
                     ?? throw new InvalidOperationException($"Нельзя упаковать корень диска: {sourceRoot}");
        progress?.Report($"Репак: упаковка {label} в {Path.GetFileName(archivePath)}…");
        var arguments = new List<string>
        {
            "a",
            "-t7z",
            archivePath,
            source.Name,
            "-mx=9",
            "-m0=lzma2",
            "-ms=on",
            "-mmt=on",
            "-bb1",
            "-bsp1",
            "-sccUTF-8",
        };
        foreach (var excluded in CommonExcludedRoots.Concat(excludedRoots).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            arguments.Add($"-x!{source.Name}\\{excluded}");
            arguments.Add($"-x!{source.Name}\\{excluded}\\*");
        }

        var result = await _toolRunner.RunAsync(
            new RepackToolCommand(sevenZipPath, parent, arguments),
            progress,
            cancellationToken);
        EnsureSucceeded(result, $"Не удалось упаковать корень {label}.");
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
        {
            throw new InvalidDataException($"7-Zip не создал {Path.GetFileName(archivePath)}.");
        }

        progress?.Report($"Репак: полная проверка {Path.GetFileName(archivePath)}…");
        var test = await _toolRunner.RunAsync(
            new RepackToolCommand(sevenZipPath, parent, ["t", "-t7z", archivePath, "-bb1", "-sccUTF-8"]),
            progress,
            cancellationToken);
        EnsureSucceeded(test, $"Проверка {Path.GetFileName(archivePath)} завершилась ошибкой.");

        var listing = await _toolRunner.RunAsync(
            new RepackToolCommand(sevenZipPath, parent, ["l", "-slt", "-sccUTF-8", archivePath]),
            cancellationToken: cancellationToken);
        EnsureSucceeded(listing, $"Не удалось прочитать структуру {Path.GetFileName(archivePath)}.");
        if (!ContainsArchiveRoot(listing.StandardOutput, source.Name))
        {
            throw new InvalidDataException(
                $"Архив {Path.GetFileName(archivePath)} не содержит ожидаемую корневую папку {source.Name}.");
        }
    }

    private static bool ContainsArchiveRoot(string listing, string rootName)
    {
        var entriesStarted = false;
        foreach (var rawLine in listing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line == "----------")
            {
                entriesStarted = true;
                continue;
            }
            if (!entriesStarted || !line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = line[7..].Replace('/', '\\').Trim('\\');
            if (entry.Equals(rootName, StringComparison.OrdinalIgnoreCase)
                || entry.StartsWith(rootName + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GenerateInstallerScript(
        RepackBuildRequest request,
        ResolvedRepackPaths paths,
        string outputDirectory,
        long gameBytes,
        long mo2Bytes)
    {
        var gameFolder = new DirectoryInfo(paths.GameSourceRoot).Name;
        var mo2Folder = request.IncludeMo2 ? new DirectoryInfo(paths.Mo2SourceRoot!).Name : string.Empty;
        var launcherRelativePath = $"{gameFolder}\\{Path.GetFileName(request.LauncherFileName)}";
        var project = EscapeDefine(request.ProjectName.Trim());
        var projectPascal = EscapePascal(request.ProjectName.Trim());
        var version = EscapeDefine(request.Version.Trim());
        var gameOnlyMb = RequiredSpaceMegabytes(gameBytes);
        var fullMb = RequiredSpaceMegabytes(checked(gameBytes + mo2Bytes));
        var template = paths.InstallerTemplateRoot;
        var icon = Path.Combine(template, "AnthologyLauncher.ico");
        var wizardImage = Path.Combine(template, "assets", "wizard-image.bmp");
        var wizardBackground = Path.Combine(template, "assets", "wizard-background.bmp");
        var sevenZipDll = Path.Combine(Path.GetDirectoryName(paths.SevenZipPath)!, "7z.dll");
        var wizardImageDirectives = File.Exists(wizardImage) && File.Exists(wizardBackground)
            ? $"WizardImageFile={EscapeDefine(wizardImage)}{Environment.NewLine}WizardSmallImageFile={EscapeDefine(wizardBackground)}"
            : string.Empty;
        var wizardImageInitialization = File.Exists(wizardImage) && File.Exists(wizardBackground)
            ? """
              BackImages := [WizardForm.WizardSmallBitmapImage.Bitmap];
              WizardSetBackImage(BackImages, True, True, 120);
              WizardForm.WizardBitmapImage.Visible := False;
              WizardForm.WizardBitmapImage2.Visible := False;
              WizardForm.WizardSmallBitmapImage.Visible := False;
              """
            : string.Empty;
        var appId = CreateStableAppId(request.ProjectName);
        var appIdValue = "{{" + appId + "}";
        var modpackComponent = request.IncludeMo2
            ? $"Name: \"modpack\"; Description: \"{EscapeDefine(mo2Folder)}\"; Types: full custom"
            : string.Empty;
        var modpackType = request.IncludeMo2
            ? "Name: \"full\"; Description: \"ANTHOLOGY + модпак\""
            : "Name: \"full\"; Description: \"Полная установка\"";
        var modpackUninstall = request.IncludeMo2
            ? $"Type: filesandordirs; Name: \"{{app}}\\{EscapeDefine(mo2Folder)}\""
            : string.Empty;

        return $$"""
            #define MyAppName "{{project}}"
            #define MyAppVersion "{{version}}"
            #define GameArchive "{{EscapeDefine(paths.GameArchiveFileName)}}"
            #define Mo2Archive "{{EscapeDefine(paths.Mo2ArchiveFileName)}}"
            #define LauncherRelativePath "{{EscapeDefine(launcherRelativePath)}}"

            [Setup]
            AppId={{appIdValue}}
            AppName={#MyAppName}
            AppVersion={#MyAppVersion}
            AppVerName={#MyAppName} {#MyAppVersion}
            AppPublisher=Anthology
            DefaultDirName={sd}\Games\{#MyAppName}
            DisableDirPage=no
            UsePreviousAppDir=no
            AlwaysShowDirOnReadyPage=yes
            DisableProgramGroupPage=yes
            OutputDir={{EscapeDefine(outputDirectory)}}
            OutputBaseFilename={{EscapeDefine(paths.SetupBaseFileName)}}
            SetupIconFile={{EscapeDefine(icon)}}
            {{wizardImageDirectives}}
            UninstallDisplayIcon={app}\{#LauncherRelativePath}
            Compression=lzma2/ultra64
            SolidCompression=yes
            WizardStyle=modern dynamic
            WizardBackColor=#101818
            WizardBackColorDynamicDark=#101818
            PrivilegesRequired=lowest
            ArchitecturesAllowed=x64compatible
            ArchitecturesInstallIn64BitMode=x64compatible
            DiskSpanning=no

            [Languages]
            Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

            [Messages]
            SelectDirDesc=Куда установить {#MyAppName}?
            SelectDirLabel3=Выберите папку проекта. Можно указать готовую папку или родительскую папку; установщик сам добавит имя проекта.
            SelectComponentsDesc=Что установить?
            SelectComponentsLabel2=Выберите состав установки.
            ReadyMemoDir=Папка установки:
            InstallingLabel=Идёт установка {#MyAppName}. Дождитесь окончания распаковки.

            [Types]
            {{modpackType}}
            Name: "gameonly"; Description: "Только ANTHOLOGY"
            Name: "custom"; Description: "Выборочная установка"; Flags: iscustom

            [Components]
            Name: "game"; Description: "ANTHOLOGY"; Types: full gameonly custom; Flags: fixed
            {{modpackComponent}}

            [Files]
            Source: "{{EscapeDefine(paths.SevenZipPath)}}"; DestDir: "{tmp}"; DestName: "7z.exe"; Flags: deleteafterinstall
            Source: "{{EscapeDefine(sevenZipDll)}}"; DestDir: "{tmp}"; DestName: "7z.dll"; Flags: deleteafterinstall

            [Icons]
            Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#LauncherRelativePath}"; WorkingDir: "{app}\{{EscapeDefine(gameFolder)}}"
            Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#LauncherRelativePath}"; WorkingDir: "{app}\{{EscapeDefine(gameFolder)}}"

            [Run]
            Filename: "{app}\{#LauncherRelativePath}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

            [UninstallDelete]
            Type: filesandordirs; Name: "{app}\{{EscapeDefine(gameFolder)}}"
            {{modpackUninstall}}

            [Code]
            const
              GameArchiveName = '{#GameArchive}';
              Mo2ArchiveName = '{#Mo2Archive}';
              RequiredGameOnlySpaceMB = {{gameOnlyMb.ToString(CultureInfo.InvariantCulture)}};
              RequiredFullInstallSpaceMB = {{fullMb.ToString(CultureInfo.InvariantCulture)}};
              ProjectDirectoryName = '{{projectPascal}}';

            function IsProjectDir(Path: string): Boolean;
            begin
              Result := CompareText(ExtractFileName(RemoveBackslashUnlessRoot(Path)), ProjectDirectoryName) = 0;
            end;

            procedure NormalizeInstallDir();
            var Dir: string;
            begin
              Dir := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
              if not IsProjectDir(Dir) then
                WizardForm.DirEdit.Text := AddBackslash(Dir) + ProjectDirectoryName;
            end;

            function SpaceText(Megabytes: Int64): string;
            begin
              Result := IntToStr((Megabytes + 999) div 1000) + ' GB';
            end;

            function IsModpackSelected(): Boolean;
            begin
              Result := {{(request.IncludeMo2 ? "WizardIsComponentSelected('modpack')" : "False")}};
            end;

            function SelectedInstallSpaceMB(): Int64;
            begin
              Result := RequiredGameOnlySpaceMB;
              if IsModpackSelected() then Result := RequiredFullInstallSpaceMB;
            end;

            procedure UpdateDiskSpaceLabel();
            begin
              WizardForm.DiskSpaceLabel.Caption := 'Требуется как минимум ' + SpaceText(SelectedInstallSpaceMB()) + ' свободного места.';
            end;

            function CheckSelectedArchives(): Boolean;
            var GameArchivePath, Mo2ArchivePath: string;
            begin
              Result := True;
              GameArchivePath := ExpandConstant('{src}\') + GameArchiveName;
              Mo2ArchivePath := ExpandConstant('{src}\') + Mo2ArchiveName;
              if not FileExists(GameArchivePath) then
              begin
                MsgBox('Рядом с установщиком не найден ' + GameArchiveName, mbError, MB_OK);
                Result := False;
                Exit;
              end;
              if IsModpackSelected() and (not FileExists(Mo2ArchivePath)) then
              begin
                MsgBox('Рядом с установщиком не найден ' + Mo2ArchiveName, mbError, MB_OK);
                Result := False;
              end;
            end;

            function CheckInstallSpace(InstallDir: string): Boolean;
            var FreeBytes, TotalBytes, RequiredBytes: Int64; DriveRoot: string;
            begin
              Result := True;
              DriveRoot := AddBackslash(ExtractFileDrive(InstallDir));
              if not GetSpaceOnDisk64(DriveRoot, FreeBytes, TotalBytes) then
              begin
                MsgBox('Не удалось проверить место на диске ' + DriveRoot, mbError, MB_OK);
                Result := False;
                Exit;
              end;
              RequiredBytes := SelectedInstallSpaceMB() * 1048576;
              if FreeBytes < RequiredBytes then
              begin
                MsgBox('Недостаточно места. Нужно минимум ' + SpaceText(SelectedInstallSpaceMB()), mbError, MB_OK);
                Result := False;
              end;
            end;

            function NextButtonClick(CurPageID: Integer): Boolean;
            begin
              Result := True;
              if CurPageID = wpSelectDir then NormalizeInstallDir()
              else if (CurPageID = wpSelectComponents) or (CurPageID = wpReady) then
              begin
                Result := CheckSelectedArchives();
                if Result then Result := CheckInstallSpace(WizardForm.DirEdit.Text);
              end;
              UpdateDiskSpaceLabel();
            end;

            procedure CurPageChanged(CurPageID: Integer);
            begin
              UpdateDiskSpaceLabel();
            end;

            function InitializeSetup(): Boolean;
            begin
              Result := FileExists(ExpandConstant('{src}\') + GameArchiveName);
              if not Result then MsgBox('Рядом с Setup не найден ' + GameArchiveName, mbError, MB_OK);
            end;

            procedure InitializeWizard();
            var BackImages: array of TGraphic;
            begin
              {{wizardImageInitialization}}
              UpdateDiskSpaceLabel();
            end;

            procedure ExtractArchive(ArchiveName, StatusText: string);
            var ResultCode: Integer; Params: string;
            begin
              WizardForm.StatusLabel.Caption := StatusText;
              WizardForm.FilenameLabel.Caption := ArchiveName;
              Params := 'x -y -aoa -bb1 -bsp1 -sccUTF-8 "' + ExpandConstant('{src}\') + ArchiveName + '" "-o' + ExpandConstant('{app}') + '"';
              if not Exec(ExpandConstant('{tmp}\7z.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Abort;
              if ResultCode <> 0 then
              begin
                MsgBox('Ошибка распаковки ' + ArchiveName + ': ' + IntToStr(ResultCode), mbError, MB_OK);
                Abort;
              end;
            end;

            procedure CurStepChanged(CurStep: TSetupStep);
            begin
              if CurStep = ssPostInstall then
              begin
                ExtractArchive(GameArchiveName, 'Распаковка игровых файлов.');
                if IsModpackSelected() then ExtractArchive(Mo2ArchiveName, 'Распаковка Mod Organizer 2 и модпака.');
              end;
            end;
            """;
    }

    private static ResolvedRepackPaths ValidateAndResolve(RepackBuildRequest request, bool requireTools = true)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
            throw new ArgumentException("Укажите название проекта.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new ArgumentException("Укажите версию репака.", nameof(request));
        }

        var game = RequireDirectory(request.GameSourceRoot, "корень игры");
        var mo2 = request.IncludeMo2 ? RequireDirectory(request.Mo2SourceRoot, "корень MO2") : null;
        var output = RequireFullPath(request.OutputRoot, "папку готового репака");
        var temporary = RequireFullPath(request.TemporaryRoot, "временную папку репака на B:");
        var sevenZip = requireTools ? RequireFile(request.SevenZipPath, "7-Zip") : Path.GetFullPath(request.SevenZipPath);
        var compiler = requireTools ? RequireFile(request.InnoSetupCompilerPath, "Inno Setup Compiler (ISCC.exe)") : Path.GetFullPath(request.InnoSetupCompilerPath);
        var template = RequireDirectory(request.InstallerTemplateRoot, "папку шаблона установщика");
        RequireTemplateFile(template, "AnthologyLauncher.ico");
        if (requireTools)
        {
            var sevenZipDll = Path.Combine(Path.GetDirectoryName(sevenZip)!, "7z.dll");
            if (!File.Exists(sevenZipDll))
            {
                throw new FileNotFoundException("Рядом с 7z.exe не найден 7z.dll.", sevenZipDll);
            }
        }

        EnsureNotDriveRoot(temporary, "Временная папка репака");
        EnsureNotDriveRoot(output, "Папка готового репака");
        EnsureSeparated(game, output, "Папка результата не может находиться внутри корня игры.");
        EnsureSeparated(game, temporary, "Временная папка не может находиться внутри корня игры.");
        if (mo2 is not null)
        {
            EnsureSeparated(mo2, output, "Папка результата не может находиться внутри корня MO2.");
            EnsureSeparated(mo2, temporary, "Временная папка не может находиться внутри корня MO2.");
        }

        return new ResolvedRepackPaths(
            game,
            mo2,
            output,
            temporary,
            sevenZip,
            compiler,
            template,
            RequireSimpleFileName(request.SetupBaseFileName, "имя Setup"),
            RequireArchiveFileName(request.GameArchiveFileName, "имя архива игры"),
            RequireArchiveFileName(request.Mo2ArchiveFileName, "имя архива MO2"));
    }

    private static long CalculateIncludedBytes(
        string root,
        HashSet<string> excludedRoots,
        CancellationToken cancellationToken)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = false,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var first = separator < 0 ? relative : relative[..separator];
            if (CommonExcludedRoots.Contains(first) || excludedRoots.Contains(first))
            {
                continue;
            }
            bytes = checked(bytes + new FileInfo(file).Length);
        }
        return bytes;
    }

    private static void EnsureFreeSpace(ResolvedRepackPaths paths, long estimatedArchiveBytes)
    {
        var temporaryDrive = new DriveInfo(Path.GetPathRoot(paths.TemporaryRoot)!);
        var outputDrive = new DriveInfo(Path.GetPathRoot(paths.OutputRoot)!);
        var reserve = Math.Max(2L * 1024 * 1024 * 1024, estimatedArchiveBytes / 20);
        var requiredTemporary = checked(estimatedArchiveBytes + reserve);
        if (temporaryDrive.AvailableFreeSpace < requiredTemporary)
        {
            throw new IOException(
                $"На временном диске {temporaryDrive.Name} недостаточно места: нужно примерно {FormatBytes(requiredTemporary)}, свободно {FormatBytes(temporaryDrive.AvailableFreeSpace)}.");
        }

        if (!string.Equals(temporaryDrive.Name, outputDrive.Name, StringComparison.OrdinalIgnoreCase)
            && outputDrive.AvailableFreeSpace < requiredTemporary)
        {
            throw new IOException(
                $"На диске результата {outputDrive.Name} недостаточно места: нужно примерно {FormatBytes(requiredTemporary)}, свободно {FormatBytes(outputDrive.AvailableFreeSpace)}.");
        }
    }

    private static async Task CommitOutputsAsync(
        IReadOnlyList<(string Source, string Destination)> outputs,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        foreach (var (_, destination) in outputs)
        {
            if (File.Exists(destination) && !overwrite)
            {
                throw new IOException($"Файл уже существует: {destination}. Включите замену или выберите другую папку.");
            }
        }

        var backups = new List<(string Original, string Backup)>();
        var committed = new List<string>();
        try
        {
            foreach (var (source, destination) in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var backup = destination + $".repack-backup-{Guid.NewGuid():N}";
                    File.Move(destination, backup);
                    backups.Add((destination, backup));
                }

                if (SameVolume(source, destination))
                {
                    File.Move(source, destination);
                }
                else
                {
                    var partial = destination + $".partial-{Guid.NewGuid():N}";
                    try
                    {
                        await CopyFileAsync(source, partial, cancellationToken);
                        File.Move(partial, destination);
                    }
                    finally
                    {
                        TryDeleteFile(partial);
                    }
                }
                committed.Add(destination);
            }

            foreach (var (_, backup) in backups)
            {
                TryDeleteFile(backup);
            }
        }
        catch
        {
            foreach (var path in committed)
            {
                TryDeleteFile(path);
            }
            foreach (var (original, backup) in backups.AsEnumerable().Reverse())
            {
                if (File.Exists(backup) && !File.Exists(original))
                {
                    File.Move(backup, original);
                }
            }
            throw;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static void EnsureSucceeded(RepackToolResult result, string message)
    {
        if (result.ExitCode == 0)
        {
            return;
        }
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? message : $"{message} {details}");
    }

    private static string RequireDirectory(string? path, string label)
    {
        var fullPath = RequireFullPath(path, label);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException($"Не найдена {label}: {fullPath}");
    }

    private static string RequireFile(string? path, string label)
    {
        var fullPath = RequireFullPath(path, label);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException($"Не найден {label}: {fullPath}", fullPath);
    }

    private static string RequireFullPath(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"Укажите {label}.");
        }
        return Path.GetFullPath(path.Trim());
    }

    private static void RequireTemplateFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"В шаблоне установщика не найден {relativePath}.", path);
        }
    }

    private static string RequireSimpleFileName(string value, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(trimmed, Path.GetFileName(trimmed), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Некорректное {label}: {value}");
        }
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static string RequireArchiveFileName(string value, string label)
    {
        var name = RequireSimpleFileName(value, label);
        return name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? name : name + ".bin";
    }

    private static void EnsureSeparated(string sourceRoot, string destinationRoot, string message)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureNotDriveRoot(string path, string label)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(trimmed) ?? string.Empty);
        if (trimmed.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} не может быть корнем диска.");
        }
    }

    private static bool SameVolume(string left, string right) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(left)), Path.GetPathRoot(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void DeleteJobDirectory(string jobRoot)
    {
        try
        {
            if (!Directory.Exists(jobRoot))
            {
                return;
            }
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobRoot));
            if (!new DirectoryInfo(full).Name.StartsWith("repack-", StringComparison.Ordinal))
            {
                return;
            }
            Directory.Delete(full, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is retried by the releaser's startup maintenance.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original build failure; startup maintenance can remove leftovers.
        }
    }

    private static long RequiredSpaceMegabytes(long bytes)
    {
        var withMargin = checked(bytes + Math.Max(1024L * 1024 * 1024, bytes / 20));
        return Math.Max(1, (withMargin + 1048575) / 1048576);
    }

    private static string EscapeDefine(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string EscapePascal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateStableAppId(string projectName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectName.Trim().ToUpperInvariant()));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes).ToString("D").ToUpperInvariant();
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024 / 1024:F1} ГБ";

    private sealed record ResolvedRepackPaths(
        string GameSourceRoot,
        string? Mo2SourceRoot,
        string OutputRoot,
        string TemporaryRoot,
        string SevenZipPath,
        string InnoSetupCompilerPath,
        string InstallerTemplateRoot,
        string SetupBaseFileName,
        string GameArchiveFileName,
        string Mo2ArchiveFileName);
}
