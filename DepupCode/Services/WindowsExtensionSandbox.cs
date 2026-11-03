using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DepupCode.Services;

/// <summary>Starts an extension runtime in a Windows AppContainer with scoped filesystem ACLs and a kill-on-close job.</summary>
public sealed class WindowsExtensionSandbox : IDisposable
{
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewConsole = 0x00000010;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint JobObjectExtendedLimitInfoClass = 9;
    private const uint JobLimitActiveProcess = 0x00000008;
    private const uint JobLimitProcessMemory = 0x00000100;
    private const uint JobLimitJobMemory = 0x00000200;
    private const uint JobLimitKillOnClose = 0x00002000;
    private const uint FileGenericRead = 0x80000000;
    private const uint FileGenericWrite = 0x40000000;
    private const uint FileGenericExecute = 0x20000000;
    private const uint FileTraverse = 0x00000020;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Delete = 0x00010000;
    private const uint InheritAce = 0x3;
    private const uint DaclSecurityInformation = 0x4;
    private const uint SeFileObject = 1;
    private const uint TrusteeIsSid = 0;
    private const uint TrusteeIsUser = 1;
    private const uint GrantAccess = 1;
    private const uint RevokeAccess = 4;
    private const uint TokenQuery = 0x0008;
    private const int TokenIsAppContainer = 29;
    private const int ErrorInsufficientBuffer = 122;

    private readonly List<string> _aclGrantedPaths = [];
    private readonly Process _process;
    private readonly IntPtr _job;
    private readonly IntPtr _appContainerSid;
    private readonly string _profileName;
    private readonly SafeFileHandle _parentInput;
    private readonly SafeFileHandle _parentOutput;
    private readonly SafeFileHandle _parentError;
    private int _disposeStarted;

    public Stream Input { get; }
    public Stream Output { get; }
    public Stream Error { get; }
    public Process Process => _process;
    public string StoragePath { get; }

    private WindowsExtensionSandbox(Process process, IntPtr job, IntPtr appContainerSid,
        SafeFileHandle parentInput, SafeFileHandle parentOutput, SafeFileHandle parentError, string storagePath, string profileName)
    {
        _process = process;
        _job = job;
        _appContainerSid = appContainerSid;
        _profileName = profileName;
        _parentInput = parentInput;
        _parentOutput = parentOutput;
        _parentError = parentError;
        StoragePath = storagePath;
        Input = parentInput.IsInvalid ? Stream.Null : new FileStream(parentInput, FileAccess.Write, 4096, isAsync: false);
        Output = parentOutput.IsInvalid ? Stream.Null : new FileStream(parentOutput, FileAccess.Read, 4096, isAsync: false);
        Error = parentError.IsInvalid ? Stream.Null : new FileStream(parentError, FileAccess.Read, 4096, isAsync: false);
    }

    public static WindowsExtensionSandbox Start(string nodeExecutable, string hostScript, string extensionDirectory,
        string workspaceRoot, string extensionId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Isolasi extension memerlukan Windows 10 versi 1809 atau lebih baru.");

        var sandbox = new WindowsExtensionSandboxBuilder(nodeExecutable, hostScript, extensionDirectory, workspaceRoot, extensionId);
        return sandbox.Start();
    }

    public static WindowsExtensionSandbox StartCommand(string executable, IReadOnlyList<string> arguments,
        string workspaceRoot, string workingDirectory, string sandboxId,
        IReadOnlyDictionary<string, string>? environment = null, IEnumerable<string>? readOnlyPaths = null,
        bool allowInternet = true, bool allowPrivateNetwork = false, IntPtr pseudoConsole = default,
        bool createNewConsole = false, string? consoleTitle = null,
        IEnumerable<string>? removedEnvironmentVariables = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Sandbox proses memerlukan Windows 10 versi 1809 atau lebih baru.");
        var root = Path.GetFullPath(workspaceRoot);
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root) || !Directory.Exists(cwd)) throw new DirectoryNotFoundException("Workspace atau working directory sandbox tidak ditemukan.");
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Workspace sandbox tidak boleh berupa root drive atau share.");
        var relative = Path.GetRelativePath(root, cwd);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Working directory proses sandbox harus berada di workspace aktif.");
        executable = Path.IsPathRooted(executable)
            ? Path.GetFullPath(executable)
            : LanguageServerService.ResolveExecutable(root, executable)
              ?? throw new FileNotFoundException($"Executable sandbox tidak ditemukan: {executable}");
        if (!File.Exists(executable) || arguments.Count > 128 || arguments.Any(argument => argument.Length > 32_768))
            throw new InvalidDataException("Executable atau argument proses sandbox tidak valid.");
        if (pseudoConsole != IntPtr.Zero && createNewConsole)
            throw new InvalidDataException("Proses tidak dapat memakai ConPTY dan console baru sekaligus.");
        return new WindowsCommandSandboxBuilder(Path.GetFullPath(executable), arguments, root, cwd,
            sandboxId, environment ?? new Dictionary<string, string>(), readOnlyPaths?.ToArray() ?? [], allowInternet,
            allowPrivateNetwork, pseudoConsole, createNewConsole, SanitizeConsoleTitle(consoleTitle),
            removedEnvironmentVariables?.ToArray() ?? []).Start();
    }

    private static string? SanitizeConsoleTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var safe = new string(title.Take(256).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return safe.Length == 0 ? null : safe;
    }

    private static void RemoveAcl(string path, string sidString)
    {
        var sid = IntPtr.Zero;
        var descriptor = IntPtr.Zero;
        var oldDacl = IntPtr.Zero;
        var newDacl = IntPtr.Zero;
        try
        {
            if (!ConvertStringSidToSid(sidString, out sid)) return;
            if (GetNamedSecurityInfo(path, SeFileObject, DaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, out oldDacl, IntPtr.Zero, out descriptor) != 0) return;
            var access = new ExplicitAccess
            {
                Mode = RevokeAccess,
                Trustee = new Trustee { TrusteeForm = TrusteeIsSid, TrusteeType = TrusteeIsUser, Name = sid }
            };
            if (SetEntriesInAcl(1, ref access, oldDacl, out newDacl) != 0) return;
            _ = SetNamedSecurityInfo(path, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
        }
        catch { }
        finally
        {
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            if (newDacl != IntPtr.Zero) LocalFree(newDacl);
        }
    }

    private static string ConvertSid(IntPtr sid)
    {
        if (!ConvertSidToStringSid(sid, out var text)) ThrowLastWin32("Membaca SID AppContainer");
        try { return Marshal.PtrToStringUni(text) ?? throw new InvalidOperationException("SID AppContainer kosong."); }
        finally { LocalFree(text); }
    }

    private static IntPtr CreateProfile(string name)
    {
        if (name.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[-_. A-Za-z0-9]+$"))
            throw new InvalidDataException($"Nama profil AppContainer tidak valid (panjang {name.Length}).");
        var hr = CreateAppContainerProfile(name, "Depup Code extension", "Isolasi runtime extension Depup Code",
            IntPtr.Zero, 0, out var sid);
        if (hr == 0) return sid;
        if (hr == unchecked((int)0x800700B7))
        {
            if (DeriveAppContainerSidFromAppContainerName(name, out sid) == 0) return sid;
        }
        throw new COMException($"AppContainer tidak dapat dibuat (HRESULT 0x{hr:X8}, panjang nama {name.Length}).", hr);
    }

    private static string MakeEnvironmentBlock(IDictionary<string, string> values, string workingDirectory)
    {
        var entries = new List<string>();
        var drive = Path.GetPathRoot(workingDirectory);
        if (drive is { Length: >= 2 } && drive[1] == ':')
            entries.Add("=" + drive[..2] + "=" + workingDirectory);
        entries.AddRange(values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value));
        return string.Join('\0', entries) + "\0\0";
    }

    private static void ThrowLastWin32(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    internal static bool IsCurrentProcessAppContainer()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        using (token)
        {
            return GetTokenInformation(token, TokenIsAppContainer, out var isAppContainer, sizeof(int), out _) &&
                   isAppContainer != 0;
        }
    }

    internal static bool IsValidEnvironmentName(string name) =>
        name.Length is > 0 and <= 128 && !name.Contains('=') && !name.Contains('\0') && !name.Any(char.IsControl);

    internal static bool IsValidEnvironmentValue(string value) =>
        value.Length <= 32_768 && !value.Contains('\0');

    private sealed class WindowsExtensionSandboxBuilder(string node, string host, string extension, string workspace, string extensionId)
    {
        public WindowsExtensionSandbox Start()
        {
            var extensionKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(extensionId)))[..24];
            var profileName = "DPC.E." + Guid.NewGuid().ToString("N");
            var sid = CreateProfile(profileName);
            WindowsExtensionSandboxBuilderResult? result = null;
            try
            {
                var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if DEBUG
                    "DepupCode-Development",
#else
                    "DepupCode",
#endif
                    "extension-sandboxes", "DepupCode.Ext." + extensionKey);
                var temp = Path.Combine(storage, "tmp");
                Directory.CreateDirectory(storage);
                Directory.CreateDirectory(temp);
                result = new WindowsExtensionSandboxBuilderResult(sid, profileName, storage, temp);
                result.Grant(Path.GetDirectoryName(node)!, write: false, execute: true);
                result.Grant(host, write: false, execute: false);
                result.Grant(extension, write: false, execute: false);
                result.Grant(storage, write: true, execute: false);
                result.Grant(workspace, write: true, execute: false);
                return result.Launch(node, [host, extension, workspace, extensionId, storage], extension,
                    workspace, extensionId, null, allowInternet: true, allowPrivateNetwork: false, pseudoConsole: IntPtr.Zero,
                    createNewConsole: false, consoleTitle: null, removedEnvironmentVariables: []);
            }
            catch
            {
                if (result is not null)
                {
                    result.RevokeAll();
                    result.Dispose();
                }
                else if (sid != IntPtr.Zero) FreeSid(sid);
                throw;
            }
        }
    }

    private sealed class WindowsCommandSandboxBuilder(string executable, IReadOnlyList<string> arguments,
        string workspace, string workingDirectory, string sandboxId, IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> readOnlyPaths, bool allowInternet, bool allowPrivateNetwork, IntPtr pseudoConsole,
        bool createNewConsole, string? consoleTitle, IReadOnlyList<string> removedEnvironmentVariables)
    {
        public WindowsExtensionSandbox Start()
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sandboxId)))[..24];
            var profileName = "DPC.R." + Guid.NewGuid().ToString("N");
            var sid = CreateProfile(profileName);
            WindowsExtensionSandboxBuilderResult? result = null;
            try
            {
                var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if DEBUG
                    "DepupCode-Development",
#else
                    "DepupCode",
#endif
                    "process-sandboxes", key);
                var temp = Path.Combine(storage, "tmp");
                Directory.CreateDirectory(storage);
                Directory.CreateDirectory(temp);
                result = new WindowsExtensionSandboxBuilderResult(sid, profileName, storage, temp);
                if (!IsDefaultWindowsSystemPath(Path.GetDirectoryName(executable)!))
                    result.Grant(Path.GetDirectoryName(executable)!, write: false, execute: true);
                foreach (var path in readOnlyPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                    if ((File.Exists(path) || Directory.Exists(path)) && !IsDefaultWindowsSystemPath(path))
                        result.Grant(path, write: false, execute: Directory.Exists(path));
                result.Grant(storage, write: true, execute: false);
                result.Grant(workspace, write: true, execute: false);
                return result.Launch(executable, arguments, workingDirectory, workspace, sandboxId,
                    environment, allowInternet, allowPrivateNetwork, pseudoConsole, createNewConsole,
                    consoleTitle, removedEnvironmentVariables);
            }
            catch
            {
                if (result is not null)
                {
                    result.RevokeAll();
                    result.Dispose();
                }
                else if (sid != IntPtr.Zero) FreeSid(sid);
                throw;
            }
        }

        private static bool IsDefaultWindowsSystemPath(string path)
        {
            var systemDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System)));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var relative = Path.GetRelativePath(systemDirectory, fullPath);
            // The Windows System32 ACL already grants ALL APPLICATION PACKAGES read/execute
            // with inheritance. Re-applying the same ACE under this tree triggers a costly ACL
            // inheritance walk across system folders and can stall terminal startup/cleanup.
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
    }

    private sealed class WindowsExtensionSandboxBuilderResult(IntPtr sid, string profileName, string storage, string temp) : IDisposable
    {
        private readonly List<string> _granted = [];
        private IntPtr _job;
        private readonly string _sidText = ConvertSid(sid);

        public void Grant(string path, bool write, bool execute)
        {
            // Windows grants SeChangeNotifyPrivilege by default, so only the requested resource needs an ACL change.
            // Avoid mutating parent ACLs: doing so can trigger expensive inheritance walks across user profile folders.
            var full = Path.GetFullPath(path);
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Sandbox tidak boleh memberi akses langsung ke root drive atau share.");
            var isDirectory = Directory.Exists(full);
            var rights = isDirectory ? FileTraverse | FileReadAttributes | FileGenericRead : FileGenericRead;
            if (execute) rights |= FileGenericExecute;
            if (write) rights |= FileGenericWrite | Delete | (isDirectory ? FileDeleteChild : 0);
            try { AddGrant(full, rights, isDirectory ? InheritAce : 0); }
            catch (Win32Exception ex) when (!write && ex.NativeErrorCode is (5 or 1314))
            {
                // Protected system/tool folders may already grant AppContainer read/execute through their machine ACL.
                // If that built-in access is insufficient, CreateProcess fails closed when the runtime loads the binary.
            }
        }

        private void AddGrant(string path, uint rights, uint inheritance)
        {
            var acl = AllocateGrantAcl(path, _sidText, rights, inheritance);
            if (acl != 0) _granted.Add(path);
        }

        public WindowsExtensionSandbox Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            string workspace, string sandboxId, IReadOnlyDictionary<string, string>? environment,
            bool allowInternet, bool allowPrivateNetwork, IntPtr pseudoConsole, bool createNewConsole,
            string? consoleTitle, IReadOnlyList<string> removedEnvironmentVariables)
        {
            LaunchAppContainerProcess(executable, arguments, workingDirectory, temp, sandboxId, storage, environment,
                allowInternet, allowPrivateNetwork, pseudoConsole, createNewConsole, consoleTitle,
                removedEnvironmentVariables, sid,
                out var input, out var output, out var error, out var process, out _job);
            WindowsExtensionSandbox sandbox;
            try { sandbox = new WindowsExtensionSandbox(process, _job, sid, input, output, error, storage, profileName); }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { process.Dispose(); } catch { }
                try { input.Dispose(); } catch { }
                try { output.Dispose(); } catch { }
                try { error.Dispose(); } catch { }
                throw;
            }
            sandbox._aclGrantedPaths.AddRange(_granted);
            return sandbox;
        }

        public void RevokeAll()
        {
            foreach (var path in _granted.AsEnumerable().Reverse()) RemoveAcl(path, _sidText);
            _granted.Clear();
        }

        public void Dispose()
        {
            if (_job != IntPtr.Zero) CloseHandle(_job);
            _ = DeleteAppContainerProfile(profileName);
            if (sid != IntPtr.Zero) FreeSid(sid);
        }

        private static int AllocateGrantAcl(string path, string sidText, uint rights, uint inheritance)
        {
            var sid = IntPtr.Zero;
            var descriptor = IntPtr.Zero;
            var oldDacl = IntPtr.Zero;
            var newDacl = IntPtr.Zero;
            try
            {
                if (!ConvertStringSidToSid(sidText, out sid)) ThrowLastWin32("SID ACL sandbox");
                var result = GetNamedSecurityInfo(path, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero,
                    out oldDacl, IntPtr.Zero, out descriptor);
                if (result != 0) throw new Win32Exception((int)result, $"Membaca ACL {path}");
                var access = new ExplicitAccess
                {
                    Permissions = rights, Mode = GrantAccess, Inheritance = inheritance,
                    Trustee = new Trustee { TrusteeForm = TrusteeIsSid, TrusteeType = TrusteeIsUser, Name = sid }
                };
                result = SetEntriesInAcl(1, ref access, oldDacl, out newDacl);
                if (result != 0) throw new Win32Exception((int)result, $"Menyusun ACL {path}");
                result = SetNamedSecurityInfo(path, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result, $"Menerapkan ACL {path}");
                return 1;
            }
            finally
            {
                if (sid != IntPtr.Zero) LocalFree(sid);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
                if (newDacl != IntPtr.Zero) LocalFree(newDacl);
            }
        }
    }

    private static void LaunchAppContainerProcess(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        string temp, string sandboxId, string storage, IReadOnlyDictionary<string, string>? extraEnvironment,
        bool allowInternet, bool allowPrivateNetwork, IntPtr pseudoConsole, bool createNewConsole,
        string? consoleTitle, IReadOnlyList<string> removedEnvironmentVariables,
        IntPtr appContainerSid, out SafeFileHandle parentInput, out SafeFileHandle parentOutput,
        out SafeFileHandle parentError, out Process process, out IntPtr job)
    {
        job = IntPtr.Zero;
        var redirectStandardHandles = pseudoConsole == IntPtr.Zero && !createNewConsole;
        var useHandleList = redirectStandardHandles;
        parentInput = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        parentOutput = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        parentError = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        SafeFileHandle childInput = null!;
        SafeFileHandle childOutput = null!;
        SafeFileHandle childError = null!;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleArray = IntPtr.Zero;
        IntPtr capabilityArray = IntPtr.Zero;
        IntPtr internetCapability = IntPtr.Zero;
        IntPtr privateNetworkCapability = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        var processInfo = default(ProcessInformation);
        var handlesTransferred = false;
        try
        {
            if (redirectStandardHandles)
            {
                CreatePipePair(out childInput, out parentInput, parentReads: false);
                CreatePipePair(out childOutput, out parentOutput, parentReads: true);
                CreatePipePair(out childError, out parentError, parentReads: true);
            }
            var attrSize = IntPtr.Zero;
            var attributeCount = 1 + (useHandleList ? 1 : 0) + (pseudoConsole != IntPtr.Zero ? 1 : 0);
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attrSize);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) ThrowLastWin32("Inisialisasi attribute AppContainer");
            attributeList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref attrSize)) ThrowLastWin32("Inisialisasi attribute AppContainer");

            var capabilityCount = (allowInternet ? 1 : 0) + (allowPrivateNetwork ? 1 : 0);
            var security = new SecurityCapabilities { AppContainerSid = appContainerSid, CapabilityCount = (uint)capabilityCount };
            if (capabilityCount > 0)
            {
                var capabilitySize = Marshal.SizeOf<SidAndAttributes>();
                capabilityArray = Marshal.AllocHGlobal(capabilitySize * capabilityCount);
                var index = 0;
                if (allowInternet)
                {
                    if (!ConvertStringSidToSid("S-1-15-3-1", out internetCapability)) ThrowLastWin32("Membuat capability internet");
                    Marshal.StructureToPtr(new SidAndAttributes { Sid = internetCapability },
                        IntPtr.Add(capabilityArray, capabilitySize * index++), fDeleteOld: false);
                }
                if (allowPrivateNetwork)
                {
                    if (!ConvertStringSidToSid("S-1-15-3-3", out privateNetworkCapability)) ThrowLastWin32("Membuat capability private network");
                    Marshal.StructureToPtr(new SidAndAttributes { Sid = privateNetworkCapability },
                        IntPtr.Add(capabilityArray, capabilitySize * index), fDeleteOld: false);
                }
                security.Capabilities = capabilityArray;
            }
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeSecurityCapabilities,
                    ref security, (IntPtr)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                ThrowLastWin32("Mengatur capability AppContainer");

            if (useHandleList)
            {
                handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(handleArray, 0, childInput.DangerousGetHandle());
                Marshal.WriteIntPtr(handleArray, IntPtr.Size, childOutput.DangerousGetHandle());
                Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, childError.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList,
                        handleArray, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                    ThrowLastWin32("Membatasi handle proses sandbox");
            }
            if (pseudoConsole != IntPtr.Zero && !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole,
                    pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowLastWin32("Menghubungkan ConPTY ke proses sandbox");

            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) ThrowLastWin32("Membuat job sandbox extension");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobLimitKillOnClose | JobLimitActiveProcess | JobLimitProcessMemory | JobLimitJobMemory,
                    ActiveProcessLimit = 64
                },
                ProcessMemoryLimit = (UIntPtr)(1024UL * 1024 * 1024),
                JobMemoryLimit = (UIntPtr)(3UL * 1024 * 1024 * 1024)
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInfoClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                ThrowLastWin32("Mengatur batas sumber daya proses sandbox");

            var startupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfoEx>(),
                Title = createNewConsole ? consoleTitle : null
            };
            if (redirectStandardHandles)
            {
                startupInfo.DwFlags = StartfUseStdHandles;
                startupInfo.HStdInput = childInput.DangerousGetHandle();
                startupInfo.HStdOutput = childOutput.DangerousGetHandle();
                startupInfo.HStdError = childError.DangerousGetHandle();
            }
            var startup = new StartupInfoEx { StartupInfo = startupInfo, AttributeList = attributeList };
            var envValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) envValues[name] = value;
            if (extraEnvironment is not null)
                foreach (var (key, value) in extraEnvironment)
                {
                    if (!IsValidEnvironmentName(key) || !IsValidEnvironmentValue(value))
                        throw new InvalidDataException("Environment variable proses sandbox tidak valid.");
                    envValues[key] = value;
                }
            foreach (var key in removedEnvironmentVariables)
            {
                if (!IsValidEnvironmentName(key)) throw new InvalidDataException("Nama environment yang dihapus tidak valid.");
                envValues.Remove(key);
            }
            envValues["TEMP"] = temp;
            envValues["TMP"] = temp;
            envValues["USERPROFILE"] = storage;
            envValues["APPDATA"] = storage;
            envValues["LOCALAPPDATA"] = storage;
            envValues["HOME"] = storage;
            envValues["DEPUP_EXTENSION_SANDBOX"] = "1";
            envValues["DEPUP_PROCESS_SANDBOX"] = "1";
            var block = MakeEnvironmentBlock(envValues, workingDirectory);
            var blockCharacters = block.ToCharArray();
            environment = Marshal.AllocHGlobal(checked(blockCharacters.Length * sizeof(char)));
            Marshal.Copy(blockCharacters, 0, environment, blockCharacters.Length);
            var commandText = Quote(executable) + (arguments.Count == 0 ? "" : " " + string.Join(" ", arguments.Select(Quote)));
            var commandLine = new StringBuilder(commandText, 32_768);
            var creationFlags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended;
            if (createNewConsole) creationFlags |= CreateNewConsole;
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, redirectStandardHandles,
                    creationFlags,
                    environment, workingDirectory, ref startup, out processInfo))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87 && IsCurrentProcessAppContainer())
                    throw new Win32Exception(error,
                        "Windows menolak pembuatan child AppContainer saat host juga berjalan di AppContainer. " +
                        "DepupCode tidak menjalankan proses ini tanpa sandbox; jalankan aplikasi dari sesi desktop biasa.");
                var handles = useHandleList
                    ? $" stdin={DescribeHandle(childInput)}, stdout={DescribeHandle(childOutput)}, stderr={DescribeHandle(childError)}"
                    : string.Empty;
                throw new Win32Exception(error, "Memulai proses dalam AppContainer." + handles);
            }
            if (!AssignProcessToJobObject(job, processInfo.Process)) ThrowLastWin32("Mengisolasi proses extension dalam job");
            if (ResumeThread(processInfo.Thread) == uint.MaxValue) ThrowLastWin32("Melanjutkan proses sandbox");
            process = Process.GetProcessById((int)processInfo.ProcessId);
            process.EnableRaisingEvents = true;
            handlesTransferred = true;
        }
        catch
        {
            if (processInfo.Process != IntPtr.Zero)
            {
                try { TerminateProcess(processInfo.Process, 1); } catch { }
                CloseHandle(processInfo.Process);
            }
            if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            throw;
        }
        finally
        {
            childInput?.Dispose(); childOutput?.Dispose(); childError?.Dispose();
            if (!handlesTransferred)
            {
                parentInput?.Dispose(); parentOutput?.Dispose(); parentError?.Dispose();
            }
            if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (capabilityArray != IntPtr.Zero) Marshal.FreeHGlobal(capabilityArray);
            if (internetCapability != IntPtr.Zero) LocalFree(internetCapability);
            if (privateNetworkCapability != IntPtr.Zero) LocalFree(privateNetworkCapability);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
            if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
            if (processInfo.Process != IntPtr.Zero) CloseHandle(processInfo.Process);
        }
    }

    private static void CreatePipePair(out SafeFileHandle child, out SafeFileHandle parent, bool parentReads)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var read, out var write, ref attributes, 0)) ThrowLastWin32("Membuat kanal extension sandbox");
        var parentHandle = parentReads ? read : write;
        var childHandle = parentReads ? write : read;
        if (!SetHandleInformation(parentHandle, 1, 0))
        {
            read.Dispose(); write.Dispose();
            ThrowLastWin32("Membatasi handle kanal extension");
        }
        child = childHandle;
        parent = parentHandle;
    }

    private static string DescribeHandle(SafeFileHandle handle)
    {
        if (!GetHandleInformation(handle, out var flags)) return $"invalid:{Marshal.GetLastWin32Error()}";
        return $"inherit:{(flags & 1) != 0}";
    }

    private static string Quote(string value)
    {
        // Preserve plain switches and tokens. In particular, cmd.exe treats quoted /c and /d as
        // command text instead of switches, then exits with code 1 without producing terminal output.
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return value;

        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') output.Append('\\', slashes * 2 + 1).Append('"');
            else { output.Append('\\', slashes).Append(character); }
            slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }

    private static void RevokeGrantedPaths(IEnumerable<string> paths, string sid)
    {
        foreach (var path in paths.Reverse()) RemoveAcl(path, sid);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { if (_job != IntPtr.Zero) CloseHandle(_job); } catch { }
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        try { Error.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
        string sid = "";
        try { sid = ConvertSid(_appContainerSid); } catch { }
        if (sid.Length > 0) RevokeGrantedPaths(_aclGrantedPaths, sid);
        _ = DeleteAppContainerProfile(_profileName);
        if (_appContainerSid != IntPtr.Zero) FreeSid(_appContainerSid);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Cb; public string? Reserved; public string? Desktop; public string? Title; public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars; public uint FillAttribute; public uint DwFlags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Pointer; public IntPtr HStdInput; public IntPtr HStdOutput; public IntPtr HStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct Trustee { public IntPtr MultipleTrustee; public uint MultipleTrusteeOperation; public uint TrusteeForm; public uint TrusteeType; public IntPtr Name; }
    [StructLayout(LayoutKind.Sequential)] private struct ExplicitAccess { public uint Permissions; public uint Mode; public uint Inheritance; public Trustee Trustee; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string profileName, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string profileName, out IntPtr appContainerSid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string profileName);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, uint attribute, ref SecurityCapabilities value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, uint attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, uint infoClass, ref JobObjectExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeFileHandle tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetNamedSecurityInfo(string name, uint objectType, uint securityInfo, IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint SetNamedSecurityInfo(string name, uint objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint SetEntriesInAcl(uint count, ref ExplicitAccess entries, IntPtr oldAcl, out IntPtr newAcl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
