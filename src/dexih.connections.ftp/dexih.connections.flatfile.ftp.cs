﻿using dexih.transforms;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using dexih.functions;
using dexih.transforms.Exceptions;
using FluentFTP;

namespace dexih.connections.ftp
{
    [Connection(
        ConnectionCategory = EConnectionCategory.File,
        Name = "Ftp Flat File", 
        Description = "Ftp File storage",
        DatabaseDescription = "Sub Directory",
        ServerDescription = "Ftp Server & Path (e.g. ftp://server/path)",
        AllowsConnectionString = false,
        AllowsSql = false,
        AllowsFlatFiles = true,
        AllowsManagedConnection = false,
        AllowsSourceConnection = true,
        AllowsTargetConnection = true,
        AllowsUserPassword = true,
        AllowsWindowsAuth = false,
        RequiresDatabase = true,
        RequiresLocalStorage = false
    )]
    public class ConnectionFlatFileFtp : ConnectionFlatFile
    {
        public override string GetFullPath(FlatFile file, EFlatFilePath path)
        {
            var fullPath = DefaultDatabase;
            if (!string.IsNullOrEmpty(file.FileRootPath))
            {
                fullPath += "/" + file.FileRootPath;
            }

            var subPath = file.GetPath(path);
            if (!string.IsNullOrEmpty(subPath))
            {
                fullPath += "/" + subPath;
            }
                
            return fullPath;
        }

        private string CombinePath(string path, string filename)
        {
            return path + "/" + filename;
        }

        private async Task<FtpClient> GetFtpClient()
        {
            bool secure;

            string[] paths;
            
            if (Server.StartsWith("ftps://"))
            {
                secure = true;
                paths = Server.Substring(7).Split('/');
            } else if (Server.StartsWith("ftp://"))
            {
                secure = false;
                paths = Server.Substring(6).Split('/');
            }
            else
            {
                throw new ConnectionException("The ftp server name must have the format ftp://server/path or ftps://server/path.");
            }

            var serverName = paths[0];
            var workingDirectory = "/" + string.Join("/", paths.Skip(1));

            var client = new FtpClient(serverName);

            if (!string.IsNullOrEmpty(Username))
            {
                client.Credentials = new NetworkCredential(Username, Password);
            }

            if (secure)
            {
                client.EncryptionMode = FtpEncryptionMode.Explicit;
                client.SslProtocols = SslProtocols.Tls;
            }

            await client.ConnectAsync();

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                client.SetWorkingDirectory(workingDirectory);
            }

            return client;
        }
        
        public override async Task<List<string>> GetFileShares()
        {
            try
            {
                using (var client = await GetFtpClient())
                {

                    var directories = new List<string>();

                    foreach (var item in client.GetListing())
                    {
                        if (item.Type == FtpFileSystemObjectType.Directory)
                        {
                            directories.Add(item.Name);
                        }
                    }

                    return directories;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred getting file shares from {Server}.  {ex.Message}", ex);
            }
        }

        public override async Task<bool> CreateDirectory(FlatFile file, EFlatFilePath path)
        {
            try
            {
                using (var client = await GetFtpClient())
                {

                    var directory = DefaultDatabase;
                    if (!client.DirectoryExists(directory))
                    {
                        await client.CreateDirectoryAsync(directory);
                    }

                    if (file != null && !string.IsNullOrEmpty(file.FileRootPath))
                    {
                        directory = CombinePath(DefaultDatabase, file.FileRootPath);
                        if (!client.DirectoryExists(directory))
                        {
                            await client.CreateDirectoryAsync(directory);
                        }
                    }

                    if (file != null && path != EFlatFilePath.None)
                    {
                        directory = CombinePath(directory, file.GetPath(path));
                        if (!client.DirectoryExists(directory))
                        {
                            await client.CreateDirectoryAsync(directory);
                        }
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred creating directory {path}.  {ex.Message}", ex);
            }
        }

        public override async Task<bool> MoveFile(FlatFile file, EFlatFilePath fromPath, EFlatFilePath toPath, string fileName)
        {
            try
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                var fileNameExtension = Path.GetExtension(fileName);
                var version = 0;
                string newFileName;

                newFileName = fileName;
                var fullToDirectory = GetFullPath(file, toPath);
                var fullFromDirectory = GetFullPath(file, fromPath);

                var createDirectoryResult = await CreateDirectory(file, toPath);
                if (!createDirectoryResult)
                {
                    return false;
                }

                using (var client = await  GetFtpClient())
                {

                    // if there is already a file with the same name on the target directory, add a version number until a unique name is found.
                    while (await client.FileExistsAsync(CombinePath(fullToDirectory, newFileName)))
                    {
                        version++;
                        newFileName = fileNameWithoutExtension + "_" + version.ToString() + fileNameExtension;
                    }

                    await client.MoveFileAsync(CombinePath(fullFromDirectory, fileName),
                        CombinePath(fullToDirectory, newFileName));
                    return true;
                }

            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred moving file {file.Name} from {fromPath} to {toPath}.  {ex.Message}", ex);
            }
        }

        public override async Task<bool> DeleteFile(FlatFile file, EFlatFilePath path, string fileName)
        {
            try
            {
                using (var client = await GetFtpClient())
                {
                    var fullDirectory = GetFullPath(file, path);
                    await client.DeleteFileAsync(CombinePath(fullDirectory, fileName));
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred deleting file {file} at {path}.  {ex.Message}", ex);
            }
        }

        public override async Task<DexihFiles> GetFileEnumerator(FlatFile file, EFlatFilePath path, string searchPattern)
        {
            try
            {
                var files = new List<DexihFileProperties>();

                using (var client = await GetFtpClient())
                {

                    var fullDirectory = GetFullPath(file, path);
                    var directoryListing = await client.GetListingAsync(fullDirectory);
                    foreach (var directoryItem in directoryListing)
                    {
                        if (directoryItem.Type == FtpFileSystemObjectType.File &&
                            (string.IsNullOrEmpty(searchPattern) || FitsMask(directoryItem.Name, searchPattern)))
                        {
                            files.Add(new DexihFileProperties()
                            {
                                FileName = directoryItem.Name,
                                LastModified = directoryItem.Modified,
                                Length = directoryItem.Size
                            });
                        }
                    }

                    var newFiles = new DexihFiles(files.ToArray());
                    return newFiles;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred getting files from {path} with pattern {searchPattern}.  {ex.Message}", ex);
            }
        }

        public override async Task<List<DexihFileProperties>> GetFileList(FlatFile file, EFlatFilePath path)
        {
            try
            {
                var files = new List<DexihFileProperties>();

                var fullDirectory = GetFullPath(file, path);
                using (var client = await GetFtpClient())
                {
                    var directoryListing = await client.GetListingAsync(fullDirectory);
                    foreach (var directoryItem in directoryListing)
                    {
                        if (directoryItem.Type == FtpFileSystemObjectType.File)
                        {
                            files.Add(new DexihFileProperties()
                            {
                                FileName = directoryItem.Name,
                                LastModified = directoryItem.Modified,
                                Length = directoryItem.Size,
                                ContentType = ""
                            });
                        }
                    }

                    return files;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred getting filelist {path}.  {ex.Message}", ex);
            }
        }

        public override async Task<Stream> GetReadFileStream(FlatFile file, EFlatFilePath path, string fileName)
        {
            try
            {
                var fullDirectory = GetFullPath(file, path);
                var client = await GetFtpClient();
                var reader = await client.OpenReadAsync(CombinePath(fullDirectory, fileName), FtpDataType.ASCII);
                var ftpStream = new FtpStream(reader, client);
                return ftpStream;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred reading file {fileName} at {path}.  {ex.Message}", ex);
            }
        }

        public override async Task<Stream> GetWriteFileStream(FlatFile file, EFlatFilePath path, string fileName)
        {
            try
            {

                await CreateDirectory(file, path);
                var client = await GetFtpClient();
                var fullDirectory = GetFullPath(file, path);
                var reader = await client.OpenWriteAsync(CombinePath(fullDirectory, fileName), FtpDataType.ASCII);

                var ftpStream = new FtpStream(reader, client);
                return ftpStream;
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred writing file {fileName} at {path}.  {ex.Message}", ex);
            }
        }

        public override async Task<bool> SaveFileStream(FlatFile file, EFlatFilePath path, string fileName, Stream stream)
        {
            try
            {
                await CreateDirectory(file, path);
                var filePath = await FixFileName(file, path, fileName);

                using (var client = await GetFtpClient())
                using (var newFile = await client.OpenWriteAsync(filePath, FtpDataType.ASCII))
                {
                    await stream.CopyToAsync(newFile);
                    await stream.FlushAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred saving file stream of file {fileName} at {path}.  {ex.Message}", ex);
            }
        }

        private async Task<string> FixFileName(FlatFile file, EFlatFilePath path, string fileName)
         {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            var fileNameExtension = Path.GetExtension(fileName);
    
            var version = 0;
    
             var fullPath = GetFullPath(file, path);
    
    
             var newFileName = fileName;
             using (var client = await GetFtpClient())
             {
                 while (await client.FileExistsAsync(CombinePath(fullPath, newFileName)))
                 {
                     version++;
                     newFileName = fileNameWithoutExtension + "_" + version.ToString() + fileNameExtension;
                 }
    
                 var filePath = CombinePath(fullPath, newFileName);
    
                 return filePath;
             }
         }

        public override async Task<bool> TestFileConnection()
        {
            try
            {
                using (var client = await GetFtpClient())
                {
                    State = EConnectionState.Open;
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new ConnectionException($"Error occurred testing if directory exists {Server}.  {ex.Message}", ex);
            }
        }

        public override async Task<bool> TableExists(Table table, CancellationToken cancellationToken)
        {
            try
            {
				var flatFile = (FlatFile)table;
                var fullPath = CombinePath(DefaultDatabase, flatFile.FileRootPath ?? "");

                using (var client = await GetFtpClient())
                {

                    var exists = client.DirectoryExists(fullPath);
                    return exists;
                }
            }
            catch(Exception ex)
            {
                throw new ConnectionException($"Error occurred testing if a directory exists for flatfile {table.Name}.  {ex.Message}", ex);
            }
        }
    }
}
