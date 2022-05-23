using System;
using System.Diagnostics.Contracts;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Toolkit.HighPerformance;
using Scarab.Interfaces;
using Scarab.Models;
using Scarab.Util;

namespace Scarab.Services
{
    public class HashMismatchException : Exception
    {
        /// <summary>
        /// The SHA256 value that was received
        /// </summary>
        public string Actual { get; }

        /// <summary>
        /// Expected SHA256 value
        /// </summary>
        public string Expected { get; }
        
        /// <summary>
        ///  The name of the object being checked
        /// </summary>
        public string Name { get; }
        
        public HashMismatchException(string name, string actual, string expected)
        {
            Name = name;
            Actual = actual;
            Expected = expected;
        }

    }
    
    public class Installer : IInstaller
    {
        private enum Update
        {
            ForceUpdate,
            LeaveUnchanged
        }

        private readonly ISettings _config;
        private readonly IModSource _installed;
        private readonly IModDatabase _db;
        private readonly IFileSystem _fs;
        
        // If we're going to have one be internal, might as well be consistent
        // ReSharper disable MemberCanBePrivate.Global 
        internal const string Modded = "winhttp.dll.v";
        internal const string Vanilla = "winhttp.dll";
        internal const string Current = "winhttp.dll";
        // ReSharper restore MemberCanBePrivate.Global

        private readonly SemaphoreSlim _semaphore = new (1);
        private readonly HttpClient _hc = new ();

        public Installer(ISettings config, IModSource installed, IModDatabase db, IFileSystem fs)
        {
            _config = config;
            _installed = installed;
            _db = db;
            _fs = fs;
        }

        private void CreateNeededDirectories()
        {
            // These both no-op if the directory already exists,
            // so no need to check ourselves
            _fs.Directory.CreateDirectory(_config.DisabledFolder);

            _fs.Directory.CreateDirectory(_config.ModsFolder);
        }

        public void Toggle(ModItem mod)
        {
            if (mod.State is not InstalledState state)
                throw new InvalidOperationException("Cannot enable mod which is not installed!");
            
            // Enable dependents when enabling a mod
            if (!state.Enabled) 
            {
                foreach (ModItem dep in mod.Dependencies.Select(x => _db.Items.First(i => i.Name == x)))
                {
                    if (dep.State is InstalledState { Enabled: true } or NotInstalledState)
                        continue;

                    Toggle(dep);
                }
            }

            CreateNeededDirectories();

            var (prev, after) = !state.Enabled
                ? (_config.DisabledFolder, _config.ModsFolder)
                : (_config.ModsFolder, _config.DisabledFolder);

            (prev, after) = (
                Path.Combine(prev, mod.Name),
                Path.Combine(after, mod.Name)
            );

            // If it's already in the other state due to user usage or an error, let it fix itself.
            if (_fs.Directory.Exists(prev) && !_fs.Directory.Exists(after))
                _fs.Directory.Move(prev, after);

            mod.State = state with { Enabled = !state.Enabled };

            _installed.RecordInstalledState(mod);
        }

        /// <remarks> This enables the API if it's installed! </remarks>
        public async Task InstallApi()
        {
            await _semaphore.WaitAsync();

            try
            {
                if (_installed.ApiInstall is InstalledState { Enabled: false })
                {
                    // Don't have the toggle update it for us, as that'll infinitely loop.
                    await _ToggleApi(Update.LeaveUnchanged);
                }

                await _InstallApi();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task _InstallApi()
        {
            // bool was_vanilla = true;

            if (_installed.ApiInstall is InstalledState { Version: var version })
            {
                if (version.Major > 0)
                    return;
            }
            
            //(string api_url, int ver, string hash) = manifest;

            string managed = _config.ManagedFolder;
            
            string api_url = "https://github.com/Schyvun/Haiku.DebugMod/releases/download/1.0.1.0/Debug.ConfigManager.Package.zip";

            (ArraySegment<byte> data, string _) = await DownloadFile(api_url, _ => { });
            
            //ThrowIfInvalidHash("the API", data, hash);

            // Backup the vanilla assembly (not needed for Haiku)
            //if (was_vanilla)
            //    _fs.File.Copy(Path.Combine(managed, Current), Path.Combine(managed, Vanilla), true);

            ExtractZip(data, managed);

            await _installed.RecordApiState(new InstalledState(true, new Version(1, 0, 0), true));
        }

        public async Task ToggleApi()
        {
            await _semaphore.WaitAsync();

            try
            {
                await _ToggleApi();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task _ToggleApi(Update update = Update.ForceUpdate)
        {
            string managed = _config.ManagedFolder;

            Contract.Assert(_installed.ApiInstall is InstalledState);

            var st = (InstalledState) _installed.ApiInstall;

            //var (move_to, move_from) = st.Enabled
            //    // If the api is enabled, move the current (modded) dll
            //    // to .m and then take from .v
            //    ? (Modded, Vanilla)
            //    // Otherwise, we're enabling the api, so move the current (vanilla) dll
            //    // And take from our .m file
            //    : (Vanilla, Modded);

            //_fs.File.Move(Path.Combine(managed, Current), Path.Combine(managed, move_to), true);
            //_fs.File.Move(Path.Combine(managed, move_from), Path.Combine(managed, Current), true);

            if (st.Enabled)
            {
                _fs.File.Move(Path.Combine(managed, Vanilla), Path.Combine(managed, Modded ), true);
            }
            else
            {
                _fs.File.Move(Path.Combine(managed, Modded), Path.Combine(managed, Vanilla), true);
            }

            await _installed.RecordApiState(st with { Enabled = !st.Enabled });

            // If we're out of date, and re-enabling the api - update it.
            // Note we do this *after* we put the API in place. (Not needed for Haiku since no "mapi" = BepInEx updates)
            //if (update == Update.ForceUpdate && !st.Enabled && st.Version.Major < _db.Api.Version)
            //    await _InstallApi();
        }

        /// <summary>
        /// Installs the given mod.
        /// </summary>
        /// <param name="mod">Mod to install</param>
        /// <param name="setProgress">Action called to indicate progress asynchronously</param>
        /// <param name="enable">Whether the mod is enabled after installation</param>
        /// <exception cref="HashMismatchException">Thrown if the download doesn't match the given hash</exception>
        public async Task Install(ModItem mod, Action<ModProgressArgs> setProgress, bool enable)
        {
            await InstallApi();

            await _semaphore.WaitAsync();

            try
            {
                CreateNeededDirectories();

                void DownloadProgressed(DownloadProgressArgs args)
                {
                    setProgress(new ModProgressArgs {
                        Download = args
                    });
                }

                // Start our progress
                setProgress(new ModProgressArgs());

                await _Install(mod, DownloadProgressed, enable);
                
                setProgress(new ModProgressArgs {
                    Completed = true
                });
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task Uninstall(ModItem mod)
        {
            await _semaphore.WaitAsync();

            try
            {
                // Shouldn't ever not exist, but rather safe than sorry I guess.
                CreateNeededDirectories();

                await _Uninstall(mod);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task _Install(ModItem mod, Action<DownloadProgressArgs> setProgress, bool enable)
        {
            foreach (ModItem dep in mod.Dependencies.Select(x => _db.Items.First(i => i.Name == x)))
            {
                if (dep.State is InstalledState { Updated: true })
                    continue;

                // Enable the dependencies' dependencies if we're enabling this mod
                // Or if the dependency was previously not installed.
                await _Install(dep, _ => { }, enable || dep.State is NotInstalledState);
            }

            var (data, filename) = await DownloadFile(mod.Link, setProgress);

            ThrowIfInvalidHash(mod.Name, data, mod.Sha256);

            // Sometimes our filename is quoted, remove those.
            filename = filename.Trim('"');
            
            string ext = Path.GetExtension(filename.ToLower());

            // Default to enabling
            string base_folder = enable
                ? _config.ModsFolder
                : _config.DisabledFolder;

            string mod_folder = Path.Combine(base_folder, mod.Name);

            switch (ext)
            {
                case ".zip":
                    {
                        ExtractZip(data, mod_folder);

                        break;
                    }

                case ".dll":
                    {
                        Directory.CreateDirectory(mod_folder);

                        await _fs.File.WriteAllBytesAsync(Path.Combine(mod_folder, filename), data.Array);

                        break;
                    }

                default:
                    {
                        throw new NotImplementedException($"Unknown file type for mod download: {filename}");
                    }
            }

            mod.State = mod.State switch {
                InstalledState installed => installed with {
                    Version = mod.Version,
                    Updated = true,
                    Enabled = enable
                },

                NotInstalledState => new InstalledState(enable, mod.Version, true),

                _ => throw new InvalidOperationException(mod.State.GetType().Name)
            };

            await _installed.RecordInstalledState(mod);
        }

        private static void ThrowIfInvalidHash(string name, ArraySegment<byte> data, string modSha256)
        {
            var sha = SHA256.Create();

            byte[] hash = sha.ComputeHash(data.AsMemory().AsStream());

            string strHash = BitConverter.ToString(hash).Replace("-", string.Empty);

            if (!string.Equals(strHash, modSha256, StringComparison.OrdinalIgnoreCase))
                throw new HashMismatchException(name, actual: strHash, expected: modSha256);
        }

        private async Task<(ArraySegment<byte> data, string filename)> DownloadFile(string uri, Action<DownloadProgressArgs> setProgress)
        {
            (ArraySegment<byte> bytes, HttpResponseMessage response) = await _hc.DownloadBytesWithProgressAsync(
                new Uri(uri), 
                new Progress<DownloadProgressArgs>(setProgress)
            );

            string? filename = string.Empty;

            if (response.Content.Headers.ContentDisposition is ContentDispositionHeaderValue disposition)
                filename = disposition.FileName;

            if (string.IsNullOrEmpty(filename))
                filename = uri[(uri.LastIndexOf("/") + 1)..];

            return (bytes, filename);
        }

        private void ExtractZip(ArraySegment<byte> data, string root)
        {
            using var archive = new ZipArchive(data.AsMemory().AsStream());

            string dest_dir_path = CreateDirectoryPath(root);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string file_dest = Path.GetFullPath(Path.Combine(dest_dir_path, entry.FullName));

                if (!file_dest.StartsWith(dest_dir_path))
                    throw new IOException("Extracts outside of directory!");

                // If it's a directory:
                if (Path.GetFileName(file_dest).Length == 0)
                {
                    _fs.Directory.CreateDirectory(file_dest);
                }
                // File
                else
                {
                    // Create containing directory:
                    _fs.Directory.CreateDirectory(Path.GetDirectoryName(file_dest)!);

                    ExtractToFile(entry, file_dest);
                }
            }
        }

        private void ExtractToFile(ZipArchiveEntry src, string dest)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            if (dest == null)
                throw new ArgumentNullException(nameof(dest));

            // Rely on FileStream's ctor for further checking dest parameter
            const FileMode fMode = FileMode.Create;

            using (Stream fs = _fs.FileStream.Create(dest, fMode, FileAccess.Write, FileShare.None, 0x1000, false))
            {
                using (Stream es = src.Open())
                    es.CopyTo(fs);
            }

            _fs.File.SetLastWriteTime(dest, src.LastWriteTime.DateTime);
        }


        private string CreateDirectoryPath(string path)
        {
            // Note that this will give us a good DirectoryInfo even if destinationDirectoryName exists:
            IDirectoryInfo di = _fs.Directory.CreateDirectory(path);

            string dest_dir_path = di.FullName;

            if (!dest_dir_path.EndsWith(Path.DirectorySeparatorChar))
                dest_dir_path += Path.DirectorySeparatorChar;

            return dest_dir_path;
        }

        private async Task _Uninstall(ModItem mod)
        {
            string dir = Path.Combine
            (
                mod.State is InstalledState { Enabled: true }
                    ? _config.ModsFolder
                    : _config.DisabledFolder,
                mod.Name
            );

            try
            {
                _fs.Directory.Delete(dir, true);
            }
            catch (DirectoryNotFoundException)
            {
                /* oh well, it's uninstalled anyways */
            }

            mod.State = new NotInstalledState();

            await _installed.RecordUninstall(mod);

            if (!_config.AutoRemoveDeps)
                return;

            foreach (ModItem dep in mod.Dependencies.Select(x => _db.Items.First(i => x == i.Name)))
            {
                // Make sure no other mods depend on it
                if (_db.Items.Where(x => x.State is InstalledState && x != mod).Any(x => x.Dependencies.Contains(dep.Name)))
                    continue;

                await _Uninstall(dep);

                dep.State = new NotInstalledState();
            }
        }
    }
}
