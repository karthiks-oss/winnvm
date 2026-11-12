using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WinNvm
{
    internal static class NvmUtils
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        // Regex to match version folder names like "v14.17.0" or "v16.13.1"
        private static readonly Regex VersionFolderRegex = new Regex(@"^v\d+\.\d+\.\d+(-\S+)?$", RegexOptions.Compiled);

        private static List<string> GetInstalledNodeVersions(string nvmHome)
        {
            var installedVersion = new List<string>();

            foreach (var dir in Directory.EnumerateDirectories(nvmHome))
            {
                // Get folder name (not full path)
                var folderName = Path.GetFileName(dir);
                if (folderName == null)
                    continue;

                if (VersionFolderRegex.IsMatch(folderName))
                {   
                    installedVersion.Add(folderName);
                }
            }

            // sort versions (lexical or semver)
            installedVersion.Sort(CompareVersionFolderNames);

            return installedVersion;
        }

        /// <summary>
        /// Compare two version folder names (e.g. "v14.17.0" vs "v16.13.1") by semantic version.
        /// Returns negative if a &lt; b.
        /// </summary>
        private static int CompareVersionFolderNames(string a, string b)
        {
            // remove leading 'v'
            string sa = a.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? a.Substring(1) : a;
            string sb = b.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? b.Substring(1) : b;

            if (Version.TryParse(sa, out var va) && Version.TryParse(sb, out var vb))
            {
                return va.CompareTo(vb);
            }
            // fallback to string compare
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }

        private static void ExtractToNvmHome(string zipFileName, string verToInstall)
        {
            var appPath = Path.Combine(Constants.NvmHome, "v" + verToInstall);
            Directory.CreateDirectory(appPath);

            var rootFolderPrefix = "node-v" + verToInstall + GetFileNameWithoutZip();

            using (var archive = ZipFile.OpenRead(zipFileName))
            {
                foreach (var entry in archive.Entries)
                {
                    var relativePath = entry.FullName;
                    if (relativePath.StartsWith(rootFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = relativePath.Substring(rootFolderPrefix.Length).TrimStart('/', '\\');
                    }

                    if (string.IsNullOrEmpty(relativePath))
                        continue;

                    var destinationPath = Path.Combine(appPath, relativePath);

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        Console.WriteLine(relativePath);
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                }
            }
        }

        private static string GetDownloadUrl(string verToInstall)
        {
            return Constants.RcFileData.NodeMirror + "v" + verToInstall + "/node-v" + verToInstall + GetFileName();
        }

        private static string GetShaSumUrl(string verToInstall)
        {
            return Constants.RcFileData.NodeMirror + "v" + verToInstall + "/SHASUMS256.txt";
        }
        
        private static string GetSavePath(string verToInstall)
        {
            return Path.Combine(Path.GetTempPath(), "v" + verToInstall + ".zip");
        }

        private static string GetFileName()
        {
            return Environment.Is64BitOperatingSystem ? "-win-x64.zip" : "-win-x86.zip";
        }

        private static string GetFileNameWithoutZip()
        {
            return Environment.Is64BitOperatingSystem ? "-win-x64" : "-win-x86";
        }
        
        internal static void PrintVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
            var version = !string.IsNullOrEmpty(infoVersion) ? infoVersion : assembly.GetName().Version?.ToString();
            Console.WriteLine($"{assembly.GetName().Name} {version}");
        }

        internal static void PrintListOfInstalledNodeVersions()
        {
            var versions = GetInstalledNodeVersions(Constants.NvmHome);

            if (versions.Count == 0)
            {
                Console.WriteLine($"No Node versions found in {Constants.NvmHome}.");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Installed Node versions:");
                Console.WriteLine();
                foreach (var version in versions)
                {
                    Console.WriteLine($"  {version}");
                }
            }
        }

        internal static void ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Usage: winnvm [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine(@"
    -i, --install <verison>    To install a new version of NodeJS
    -l, --list                 To list all installed versions of NodeJS
    -u, --use <version>        To use the given version of NodeJS
    -r, --remove <version>     To uninstall a version of NodeJS
    -h, --help                 Show this message
    -v, --version              Display Version");
        }

        internal static void LoadRcJson()
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var rcFile = Path.Combine(homePath, Constants.RcFileName);

            if (File.Exists(rcFile))
            {
                var json = File.ReadAllText(rcFile);
                Constants.RcFileData = JsonConvert.DeserializeObject<RcFileData>(json);
            }

            if (Constants.RcFileData == null || string.IsNullOrEmpty(Constants.RcFileData.NodeMirror))
            {
                Constants.RcFileData = new RcFileData {NodeMirror = "https://nodejs.org/dist/"};
            }

            if (!Constants.RcFileData.NodeMirror.EndsWith("/"))
            {
                Constants.RcFileData.NodeMirror = Constants.RcFileData.NodeMirror + "/";
            }
        }
        
        private static string GetChecksumFile(string fileName)
        {
            using (var fileStream = File.OpenRead(fileName))
            using (var sha = SHA256.Create())
            {
                var checksum = sha.ComputeHash(fileStream);
                return Convert.ToHexString(checksum);
            }
        }
        
        private static void ValidateSha256Sum(string fileNameForSaving, string shaInfo)
        {
            if (shaInfo == null) throw new ArgumentNullException(nameof(shaInfo));
            var shaSumForFile = GetChecksumFile(fileNameForSaving);
            if (!shaInfo.Equals(shaSumForFile, StringComparison.OrdinalIgnoreCase))
            {
                throw new WinNvmException("SHA256 Checksum failed");
            }
        }

        internal static void ValidateNodeVersionAndDownload(string verToInstall)
        {
            var urlToDownload = Constants.RcFileData.NodeMirror + "index.json";
            string json;
            try
            {
                var response = HttpClient.GetAsync(urlToDownload).GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new WinNvmException("Cannot access " + urlToDownload +
                                              ".\nPlease check the NodeMirror property in " + Constants.RcFileName +
                                              " in your home directory.\nAlso check you network connection");
                response.EnsureSuccessStatusCode();
                json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (HttpRequestException exception)
            {
                throw new WinNvmException(exception.Message);
            }

            var versionJson = JsonConvert.DeserializeObject<List<NodeVersions>>(json).OrderBy(o => o.Date).ToList();
            var tmpVersion = versionJson.Where(v => v.Version.Equals('v' + verToInstall));
            var nodeVersionses = tmpVersion as NodeVersions[] ?? tmpVersion.ToArray();

            if (!nodeVersionses.Any())
                throw new WinNvmException("Node version " + verToInstall + " is not available");

            urlToDownload = GetDownloadUrl(verToInstall);
            
            var shaSumsContent = HttpClient.GetStringAsync(GetShaSumUrl(verToInstall)).GetAwaiter().GetResult();
            var shaSums = shaSumsContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var theLine = shaSums.Single(line => line.EndsWith("node-v" + verToInstall + GetFileName()));
            var shaInfo = Regex.Split(theLine, @"\s+")[0];
            var fileNameForSaving = GetSavePath(verToInstall);
            Console.WriteLine("Downloading :{0} -> {1}", urlToDownload, fileNameForSaving);

            using (var responseStream = HttpClient.GetStreamAsync(urlToDownload).GetAwaiter().GetResult())
            using (var fileStream = new FileStream(fileNameForSaving, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                responseStream.CopyTo(fileStream);
            }
            Console.WriteLine("Downloaded");

            ValidateSha256Sum(fileNameForSaving, shaInfo);

            Console.WriteLine("Extracting");
            ExtractToNvmHome(fileNameForSaving, verToInstall);
            Console.WriteLine("Folder Extracted");
        }

        internal static void ValidateEnvironment()
        {
            Constants.NvmHome = Environment.GetEnvironmentVariable(Constants.NvmHomeVarName);
            Constants.NvmSymLink = Environment.GetEnvironmentVariable(Constants.NvmSymLinkVarName);

            if (string.IsNullOrEmpty(Constants.NvmHome))
                throw new WinNvmException(Constants.NvmHomeVarName +
                                          " is not defined please create a environment variable named " +
                                          Constants.NvmHomeVarName);

            if (string.IsNullOrEmpty(Constants.NvmSymLink))
                throw new WinNvmException(Constants.NvmSymLinkVarName +
                                          " is not defined please create a environment variable named " +
                                          Constants.NvmSymLinkVarName);

            if (!Constants.NvmHome.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                Constants.NvmHome = Constants.NvmHome + Path.DirectorySeparatorChar;
            }
            if (!Constants.NvmSymLink.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                Constants.NvmSymLink = Constants.NvmSymLink + Path.DirectorySeparatorChar;
            }
        }

        internal static void ValidateNodeVersionAndUse(string verToUse)
        {
            var src = Constants.NvmHome + 'v' + verToUse;
            var dest = Constants.NvmSymLink;

            if (!Directory.Exists(src))
            {
                throw new WinNvmException("Version " + verToUse + " is not installed.");
            }

            if (Directory.Exists(dest))
            {
                Directory.Delete(dest);
            }

            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

            Console.WriteLine("{0} {1}", cmd, "/c mklink /J " + dest + " " + src);

            var psInfo = new ProcessStartInfo(cmd, "/c mklink /J " + dest + " " + src)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            var getIpInfo = Process.Start(psInfo);

            if (getIpInfo == null) return;
            var myOutput = getIpInfo.StandardOutput;
            getIpInfo.WaitForExit(3000);
            if (getIpInfo.HasExited)
            {
                Console.WriteLine(myOutput.ReadToEnd());
            }
        }

        internal static void UninstallNodeJs(string verToUse)
        {
            var src = Constants.NvmHome + 'v' + verToUse;
            if (Directory.Exists(src))
            {
                Directory.Delete(src, true);
                Console.WriteLine(verToUse + " is uninstalled");
            }
            else
            {
                throw new WinNvmException("Version " + verToUse + " not installed");
            }
        }
    }
}
