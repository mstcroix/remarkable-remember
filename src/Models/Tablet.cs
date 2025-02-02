using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReMarkableRemember.Helper;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ReMarkableRemember.Models;

internal sealed class Tablet : IDisposable
{
    private const String IP = "10.11.99.1";
    private const String PATH_NOTEBOOKS = "/home/root/.local/share/remarkable/xochitl/";
    private const String PATH_TEMPLATES = "/usr/share/remarkable/templates/";
    private const String PATH_TEMPLATES_FILE = "templates.json";
    private const Int32 SSH_TIMEOUT = 2;
    private const String SSH_USER = "root";
    private const Int32 USB_TIMEOUT = 1;

    private static readonly JsonSerializerOptions jsonSerializerOptions = new JsonSerializerOptions() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly HttpClient gitHubClient;
    private readonly Settings settings;
    private readonly SemaphoreSlim sshSemaphore;
    private readonly HttpClient usbClient;
    private readonly HttpClient usbClientConnection;
    private readonly SemaphoreSlim usbSemaphore;

    public Tablet(Settings settings)
    {
        this.gitHubClient = new HttpClient();
        this.settings = settings;
        this.sshSemaphore = new SemaphoreSlim(1, 1);
        this.usbClient = new HttpClient();
        this.usbClientConnection = new HttpClient() { Timeout = TimeSpan.FromSeconds(USB_TIMEOUT) };
        this.usbSemaphore = new SemaphoreSlim(1, 1);
    }

    public async Task Backup(String id, String targetDirectory)
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);

            await BackupFiles(client, PATH_NOTEBOOKS, targetDirectory, file => file.Name.StartsWith(id, StringComparison.Ordinal)).ConfigureAwait(false);
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public async Task DeleteTemplate(TabletTemplate template)
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);

            String templatesFilePath = $"{PATH_TEMPLATES}{PATH_TEMPLATES_FILE}";
            String templatesFileText = await Task.Run(() => client.ReadAllText(templatesFilePath)).ConfigureAwait(false);
            TemplatesFile templatesFile = JsonSerializer.Deserialize<TemplatesFile>(templatesFileText, jsonSerializerOptions);

            Int32 index = templatesFile.Templates.FindIndex((item) => String.CompareOrdinal(item.Filename, template.FileName) == 0);
            if (index > -1)
            {
                templatesFile.Templates.RemoveAt(index);
            }

            await FileDelete(client, $"{PATH_TEMPLATES}{template.FileName}.png").ConfigureAwait(false);
            await FileDelete(client, $"{PATH_TEMPLATES}{template.FileName}.svg").ConfigureAwait(false);
            await FileWrite(client, templatesFilePath, JsonSerializer.Serialize(templatesFile, jsonSerializerOptions)).ConfigureAwait(false);
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public void Dispose()
    {
        this.gitHubClient.Dispose();
        this.sshSemaphore.Dispose();
        this.usbClient.Dispose();
        this.usbClientConnection.Dispose();
        this.usbSemaphore.Dispose();

        GC.SuppressFinalize(this);
    }

    public async Task<Stream> Download(String id)
    {
        await this.usbSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            return await ExecuteHttp(() => this.usbClient.GetStreamAsync(new Uri($"http://{IP}/download/{id}/placeholder"))).ConfigureAwait(false);
        }
        finally
        {
            this.usbSemaphore.Release();
        }
    }

    public async Task<TabletConnectionError?> GetConnectionStatus()
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);
        }
        catch (TabletException exception)
        {
            return exception.Error;
        }
        finally
        {
            this.sshSemaphore.Release();
        }

        await this.usbSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            await ExecuteHttp(() => this.usbClientConnection.GetStringAsync(new Uri($"http://{IP}/documents/"))).ConfigureAwait(false);
        }
        catch (TabletException exception)
        {
            return exception.Error;
        }
        finally
        {
            this.usbSemaphore.Release();
        }

        return null;
    }

    public async Task<IEnumerable<Item>> GetItems()
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);

            IEnumerable<ISftpFile> files = await Task.Run(() => client.ListDirectory(PATH_NOTEBOOKS)).ConfigureAwait(false);

            List<Item> allItems = new List<Item>();
            foreach (ISftpFile file in files.Where(file => file.IsRegularFile && file.Name.EndsWith(".metadata", StringComparison.Ordinal)))
            {
                String metaDataFileText = await Task.Run(() => client.ReadAllText(file.FullName)).ConfigureAwait(false);
                MetaDataFile metaDataFile = JsonSerializer.Deserialize<MetaDataFile>(metaDataFileText, jsonSerializerOptions);
                if (metaDataFile.Deleted != true)
                {
                    String id = Path.GetFileNameWithoutExtension(file.Name);
                    allItems.Add(new Item(id, metaDataFile.LastModified, metaDataFile.Parent, metaDataFile.Type, metaDataFile.VisibleName));
                }
            }

            IEnumerable<Item> items = allItems.Where(item => String.IsNullOrEmpty(item.ParentCollectionId) || item.Trashed);
            foreach (Item item in items) { UpdateItems(item, allItems); }
            return items;
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public async Task<Notebook> GetNotebook(String id)
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);

            String contentFileText = await Task.Run(() => client.ReadAllText($"{PATH_NOTEBOOKS}{id}.content")).ConfigureAwait(false);
            ContentFile contentFile = JsonSerializer.Deserialize<ContentFile>(contentFileText, jsonSerializerOptions);

            if (contentFile.FileType != "notebook") { throw new NotebookException("Invalid reMarkable file type."); }
            if (contentFile.FormatVersion is not (1 or 2)) { throw new NotebookException($"Invalid reMarkable file format version: '{contentFile.FormatVersion}'."); }

            List<Byte[]> pageBuffers = new List<Byte[]>();
            IEnumerable<String> pages = (contentFile.FormatVersion == 1) ? contentFile.Pages : contentFile.CPages.Pages.Where(page => page.Deleted == null).Select(page => page.Id);
            foreach (String page in pages)
            {
                Byte[] pageBuffer = await Task.Run(() => client.ReadAllBytes($"{PATH_NOTEBOOKS}{id}/{page}.rm")).ConfigureAwait(false);
                pageBuffers.Add(pageBuffer);
            }
            return new Notebook(pageBuffers, contentFile.Orientation == "portrait");
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public async Task InstallLamyEraser(Boolean press, Boolean undo, Boolean leftHanded)
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient sftpClient = await this.CreateSftpClient().ConfigureAwait(false);
            using SshClient sshClient = await this.CreateSshClient().ConfigureAwait(false);

            await ExecuteSshCommand(sshClient, "systemctl stop LamyEraser.service 2&> /dev/null", false).ConfigureAwait(false);
            await ExecuteSshCommand(sshClient, "systemctl disable LamyEraser.service 2&> /dev/null", false).ConfigureAwait(false);

            String serviceText = await this.gitHubClient.GetStringAsync(new Uri("https://raw.githubusercontent.com/isaacwisdom/RemarkableLamyEraser/v1/RemarkableLamyEraser/LamyEraser.service")).ConfigureAwait(false);
            serviceText = InstallLamyEraserOptions(serviceText, press, undo, leftHanded);
            await FileWrite(sftpClient, "/lib/systemd/system/LamyEraser.service", serviceText).ConfigureAwait(false);

            Byte[] serviceBytes = await this.gitHubClient.GetByteArrayAsync(new Uri("https://raw.githubusercontent.com/isaacwisdom/RemarkableLamyEraser/v1/RemarkableLamyEraser/RemarkableLamyEraser")).ConfigureAwait(false);
            await FileWrite(sftpClient, "/usr/sbin/RemarkableLamyEraser", serviceBytes).ConfigureAwait(false);

            await ExecuteSshCommand(sshClient, "chmod +x /usr/sbin/RemarkableLamyEraser").ConfigureAwait(false);
            await ExecuteSshCommand(sshClient, "systemctl daemon-reload").ConfigureAwait(false);
            await ExecuteSshCommand(sshClient, "systemctl enable LamyEraser.service").ConfigureAwait(false);
            await ExecuteSshCommand(sshClient, "systemctl start LamyEraser.service").ConfigureAwait(false);
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public async Task Restart()
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SshClient client = await this.CreateSshClient().ConfigureAwait(false);
            await ExecuteSshCommand(client, "systemctl restart xochitl").ConfigureAwait(false);
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    public async Task UploadFile(String path, String? parentId)
    {
        await this.usbSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            await ExecuteHttp(() => this.usbClient.GetStringAsync(new Uri($"http://{IP}/documents/{parentId}"))).ConfigureAwait(false);

            FileInfo file = new FileInfo(path);
            String mediaType = UploadFileCheck(file);

            using StreamContent fileContent = new StreamContent(File.OpenRead(path));
            fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = $"\"{file.Name}\"" };
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);

            using MultipartFormDataContent multipartContent = new MultipartFormDataContent() { { fileContent } };

            HttpResponseMessage response = await ExecuteHttp(() => this.usbClient.PostAsync(new Uri($"http://{IP}/upload"), multipartContent)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            this.usbSemaphore.Release();
        }
    }

    public async Task UploadTemplate(TabletTemplate template)
    {
        await this.sshSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            using SftpClient client = await this.CreateSftpClient().ConfigureAwait(false);

            String templatesFilePath = $"{PATH_TEMPLATES}{PATH_TEMPLATES_FILE}";
            String templatesFileText = await Task.Run(() => client.ReadAllText(templatesFilePath)).ConfigureAwait(false);
            TemplatesFile templatesFile = JsonSerializer.Deserialize<TemplatesFile>(templatesFileText, jsonSerializerOptions);

            Int32 index = templatesFile.Templates.FindIndex((item) => String.CompareOrdinal(item.Filename, template.FileName) == 0);
            if (index > -1)
            {
                templatesFile.Templates[index] = TemplatesFile.Template.Convert(template);
            }
            else
            {
                templatesFile.Templates.Add(TemplatesFile.Template.Convert(template));
            }

            await FileWrite(client, $"{PATH_TEMPLATES}{template.FileName}.png", template.BytesPng).ConfigureAwait(false);
            await FileWrite(client, $"{PATH_TEMPLATES}{template.FileName}.svg", template.BytesSvg).ConfigureAwait(false);
            await FileWrite(client, templatesFilePath, JsonSerializer.Serialize(templatesFile, jsonSerializerOptions)).ConfigureAwait(false);
        }
        finally
        {
            this.sshSemaphore.Release();
        }
    }

    private static async Task BackupFiles(SftpClient client, String sourceDirectory, String targetDirectory, Func<ISftpFile, Boolean> filter)
    {
        IEnumerable<ISftpFile> files = await Task.Run(() => client.ListDirectory(sourceDirectory)).ConfigureAwait(false);
        foreach (ISftpFile file in files.Where(filter))
        {
            String targetPath = Path.Combine(targetDirectory, file.Name);

            if (file.IsDirectory)
            {
                await BackupFiles(client, file.FullName, targetPath, file => file.Name is not "." and not "..").ConfigureAwait(false);
            }

            if (file.IsRegularFile)
            {
                using Stream fileStream = FileSystem.Create(targetPath);
                await Task.Run(() => client.DownloadFile(file.FullName, fileStream)).ConfigureAwait(false);
            }
        }
    }

    private async Task<SftpClient> CreateSftpClient()
    {
        SftpClient client = new SftpClient(this.CreateSshConnectionInfo());
        await ConnectClient(client).ConfigureAwait(false);
        return client;
    }

    private async Task<SshClient> CreateSshClient()
    {
        SshClient client = new SshClient(this.CreateSshConnectionInfo());
        await ConnectClient(client).ConfigureAwait(false);
        return client;
    }

    private ConnectionInfo CreateSshConnectionInfo()
    {
        String host = String.IsNullOrEmpty(this.settings.TabletIp) ? IP : this.settings.TabletIp;
        AuthenticationMethod authenticationMethod = new PasswordAuthenticationMethod(SSH_USER, this.settings.TabletPassword);
        return new ConnectionInfo(host, SSH_USER, authenticationMethod) { Timeout = TimeSpan.FromSeconds(SSH_TIMEOUT) };
    }

    private static async Task ConnectClient(BaseClient client)
    {
        try
        {
            await Task.Run(client.Connect).ConfigureAwait(false);
        }
        catch (ProxyException exception)
        {
            throw new TabletException(exception.Message, exception);
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode is SocketError.ConnectionRefused)
            {
                throw new TabletException(TabletConnectionError.SshNotConfigured, "SSH protocol information are not configured or wrong.", exception);
            }

            if (exception.SocketErrorCode is SocketError.HostDown or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.NetworkUnreachable)
            {
                throw new TabletException(TabletConnectionError.SshNotConnected, "reMarkable is not connected via WiFi or USB.", exception);
            }

            throw new TabletException(exception.Message, exception);
        }
        catch (SshAuthenticationException exception)
        {
            throw new TabletException(TabletConnectionError.SshNotConfigured, "SSH protocol information are not configured or wrong.", exception);
        }
        catch (SshOperationTimeoutException exception)
        {
            throw new TabletException(TabletConnectionError.SshNotConnected, "reMarkable is not connected via WiFi or USB.", exception);
        }
    }

    private static async Task<T> ExecuteHttp<T>(Func<Task<T>> httpClientRequest)
    {
        try
        {
            return await httpClientRequest().ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            if (exception.InnerException is SocketException socketException)
            {
                if (socketException.SocketErrorCode is SocketError.ConnectionRefused)
                {
                    throw new TabletException(TabletConnectionError.UsbNotActived, "USB web interface is not activated.", exception);
                }

                if (socketException.SocketErrorCode is SocketError.HostDown or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.NetworkUnreachable)
                {
                    throw new TabletException(TabletConnectionError.UsbNotConnected, "reMarkable is not connected via USB.", exception);
                }
            }

            throw new TabletException(exception.Message, exception);
        }
        catch (TaskCanceledException exception)
        {
            throw new TabletException(TabletConnectionError.UsbNotConnected, "reMarkable is not connected via USB.", exception);
        }
    }

    private static async Task ExecuteSshCommand(SshClient client, String command, Boolean checkResult = true)
    {
        SshCommand result = await Task.Run(() => client.RunCommand(command)).ConfigureAwait(false);
        if (checkResult && result.ExitStatus != 0)
        {
            throw new TabletException(result.Error);
        }
    }

    private static async Task FileDelete(SftpClient client, String path)
    {
        await Task.Run(() => { if (client.Exists(path)) { client.DeleteFile(path); } }).ConfigureAwait(false);
    }

    private static async Task FileWrite(SftpClient client, String path, Object content)
    {
        await FileDelete(client, path).ConfigureAwait(false);

        if (content is String text)
        {
            await Task.Run(() => client.WriteAllText(path, text)).ConfigureAwait(false);
        }
        else if (content is Byte[] bytes)
        {
            await Task.Run(() => client.WriteAllBytes(path, bytes)).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    private static String InstallLamyEraserOptions(String serviceText, Boolean press, Boolean undo, Boolean leftHanded)
    {
        String pressText = press ? " --press" : " --toggle";
        String undoText = undo ? " --double-press undo" : " --double-press redo";
        String leftHandedText = leftHanded ? " --left-handed" : String.Empty;

        return serviceText.Replace(
            "ExecStart=/usr/sbin/RemarkableLamyEraser",
            $"ExecStart=/usr/sbin/RemarkableLamyEraser{pressText}{undoText}{leftHandedText}",
            StringComparison.Ordinal
        );
    }

    private static void UpdateItems(Item parentItem, IEnumerable<Item> allItems)
    {
        IEnumerable<Item> children = allItems.Where(item => item.ParentCollectionId == parentItem.Id);
        foreach (Item child in children)
        {
            child.Trashed = parentItem.Trashed;
            parentItem.Collection?.Add(child);

            UpdateItems(child, allItems);
        }
    }

    private static String UploadFileCheck(FileInfo file)
    {
        if (file.Length >= 100 * 1024 * 1024) { throw new NotSupportedException("File is to large."); }

        switch (file.Extension.ToUpperInvariant())
        {
            case ".PDF": return "application/pdf";
            case ".EPUB": return "application/epub+zip";
            default: throw new NotSupportedException("File type is not supported.");
        }
    }

    private struct ContentFile
    {
        public PagesContainer CPages { get; set; }
        public String FileType { get; set; }
        public Int32 FormatVersion { get; set; }
        public String Orientation { get; set; }
        public IEnumerable<String> Pages { get; set; }

        public struct PagesContainer
        {
            public Collection<Page> Pages { get; set; }

            public struct Page
            {
                public Object? Deleted { get; set; }
                public String Id { get; set; }
            }
        }
    }

    private struct MetaDataFile
    {
        public Boolean? Deleted { get; set; }
        public String LastModified { get; set; }
        public String Parent { get; set; }
        public String Type { get; set; }
        public String VisibleName { get; set; }
    }

    internal sealed class Item
    {
        public Item(String id, String lastModified, String parent, String type, String visibleName)
        {
            this.Collection = (type == "CollectionType") ? new List<Item>() : null;
            this.Id = id;
            this.Modified = DateTime.UnixEpoch.AddMilliseconds(Double.Parse(lastModified, CultureInfo.InvariantCulture));
            this.Name = (type == "DocumentType") ? $"{visibleName}.pdf" : visibleName;
            this.ParentCollectionId = parent;
            this.Trashed = parent == "trash";
        }

        public List<Item>? Collection { get; }
        public String Id { get; }
        public DateTime Modified { get; }
        public String Name { get; }
        public String ParentCollectionId { get; }
        public Boolean Trashed { get; set; }
    }

    private struct TemplatesFile
    {
        public List<Template> Templates { get; set; }

        public struct Template
        {
            public IEnumerable<String> Categories { get; set; }
            public String Filename { get; set; }
            public String IconCode { get; set; }
            public Boolean? Landscape { get; set; }
            public String Name { get; set; }

            public static Template Convert(TabletTemplate template)
            {
                return new Template()
                {
                    Categories = new List<String>() { template.Category },
                    Filename = template.FileName,
                    IconCode = template.IconCode,
                    Landscape = TabletTemplate.IsLandscape(template.IconCode),
                    Name = template.Name
                };
            }
        }
    }
}
