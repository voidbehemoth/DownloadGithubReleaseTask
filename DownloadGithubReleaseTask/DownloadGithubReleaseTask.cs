

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Octokit;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadGithubReleaseTask
{
    public class DownloadGithubRelease : Microsoft.Build.Utilities.Task
    {
        /// <summary>
        /// Gets or sets an optional filename for the destination file.  By default, the filename is derived from the <see cref="SourceUrl"/> if possible.
        /// </summary>
        public ITaskItem DestinationFileName { get; set; }

        /// <summary>
        /// Gets or sets a <see cref="ITaskItem"/> that specifies the destination folder to download the file to.
        /// </summary>
        [Required]
        public ITaskItem DestinationFolder { get; set; }

        /// <summary>
        /// Gets or sets a <see cref="ITaskItem"/> that contains details about the downloaded release.
        /// </summary>
        [Output]
        public ITaskItem DownloadedRelease { get; set; }

        /// <summary>
        /// Gets or sets the name of the repo to get the release from. Format is username/repo_name.
        /// </summary>
        [Required]
        public string RepoName { get; set; }

        /// <summary>
        /// Gets or sets whether to automatically use the latest release or not. If false, please include TagName.
        /// </summary>
        public bool GetLatest { get; set; }

        /// <summary>
        /// Gets or sets the name of the file to be downloaded.
        /// </summary>
        [Required]
        public string ReleaseFileName { get; set; }

        /// <summary>
        /// Gets or sets the name of the tag to get the release file from. Do not omit if GetLatest is set to false.
        /// </summary>
        public string TagName { get; set; }
        internal HttpMessageHandler HttpMessageHandler { get; set; }

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        private async Task<bool> ExecuteAsync()
        {
            var client = new GitHubClient(new ProductHeaderValue("Download-Github-Releases-Task"));

            var (success, release) = GetRelease(client);
            var assets = release.Assets;

            if (!success || assets == null) return false;

            bool matches = assets.Any(releaseAsset => releaseAsset.Name.Equals(ReleaseFileName));

            if (!matches) return false;

            ReleaseAsset asset = assets.First(releaseAsset => releaseAsset.Name.Equals(ReleaseFileName));

            if (asset == null) return false;

            await DownloadAsync(new Uri(asset.BrowserDownloadUrl));

            return DownloadedRelease != null;
        }

        private async System.Threading.Tasks.Task DownloadAsync(Uri uri)
        {
            using (var client = new HttpClient(HttpMessageHandler ?? new HttpClientHandler(), disposeHandler: true))
            {
                using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!TryGetFileName(response, out string filename))
                    {
                        Log.LogErrorWithCodeFromResources("DownloadFile.ErrorUnknownFileName", uri, nameof(DestinationFileName));
                        return;
                    }

                    DirectoryInfo destinationDirectory = Directory.CreateDirectory(DestinationFolder.ItemSpec);

                    var destinationFile = new FileInfo(Path.Combine(destinationDirectory.FullName, filename));

                    if (ShouldSkip(response, destinationFile))
                    {
                        Log.LogMessageFromResources(MessageImportance.Normal, "DownloadFile.DidNotDownloadBecauseOfFileMatch", uri, destinationFile.FullName, "true");

                        DownloadedRelease = new TaskItem(destinationFile.FullName);

                        return;
                    }
                    try
                    {
                        using (var target = new FileStream(destinationFile.FullName, System.IO.FileMode.Create, FileAccess.Write))
                        {
                            using (Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                await responseStream.CopyToAsync(target, 1024).ConfigureAwait(false);
                            }

                            DownloadedRelease = new TaskItem(destinationFile.FullName);
                        }
                    } finally
                    {
                        if (DownloadedRelease == null) destinationFile.Delete();
                    }
                    return;
                }
            }

        }

        private bool TryGetFileName(HttpResponseMessage response, out string filename)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            filename = !String.IsNullOrWhiteSpace(DestinationFileName?.ItemSpec)
                ? DestinationFileName.ItemSpec // Get the file name from what the user specified
                : response.Content?.Headers?.ContentDisposition?.FileName // Attempt to get the file name from the content-disposition header value
                  ?? Path.GetFileName(response.RequestMessage.RequestUri.LocalPath); // Otherwise attempt to get a file name from the URI

            return !string.IsNullOrWhiteSpace(filename);
        }

        private bool ShouldSkip(HttpResponseMessage response, FileInfo destinationFile)
        {
            return destinationFile.Exists
                   && destinationFile.Length == response.Content.Headers.ContentLength
                   && response.Content.Headers.LastModified.HasValue
                   && destinationFile.LastWriteTimeUtc > response.Content.Headers.LastModified.Value.UtcDateTime;
        }

    private (bool, Release) GetRelease(GitHubClient client)
        {
            var splitRepo = RepoName.Split('/');
            string owner = splitRepo[0];
            string repoName = splitRepo[1];

            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repoName)) return (false, null);

            try
            {
                if (!GetLatest) 
                {
                    if (string.IsNullOrEmpty(TagName)) return (false, null);

                    return (true, client.Repository.Release.Get(owner, repoName, TagName).Result);
                } 

                return (true, client.Repository.Release.GetAll(owner, repoName).Result[0]);
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, showStackTrace: true);
                return (false, null);
            }
        }
    }
}
