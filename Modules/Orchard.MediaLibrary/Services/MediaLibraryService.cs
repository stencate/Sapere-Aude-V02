﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using Orchard.ContentManagement;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.Core.Common.Models;
using Orchard.FileSystems.Media;
using Orchard.Localization;
using Orchard.MediaLibrary.Factories;
using Orchard.MediaLibrary.Models;
using Orchard.Core.Title.Models;
using Orchard.Validation;

namespace Orchard.MediaLibrary.Services {
    public class MediaLibraryService : IMediaLibraryService {
        private readonly IOrchardServices _orchardServices;
        private readonly IMimeTypeProvider _mimeTypeProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly IEnumerable<IMediaFactorySelector> _mediaFactorySelectors;

        public MediaLibraryService(
            IOrchardServices orchardServices, 
            IMimeTypeProvider mimeTypeProvider,
            IStorageProvider storageProvider,
            IEnumerable<IMediaFactorySelector> mediaFactorySelectors) {
            _orchardServices = orchardServices;
            _mimeTypeProvider = mimeTypeProvider;
            _storageProvider = storageProvider;
            _mediaFactorySelectors = mediaFactorySelectors;

            T = NullLocalizer.Instance;
        }

        public Localizer T { get; set; }

        public IEnumerable<ContentTypeDefinition> GetMediaTypes() {
            return _orchardServices
                .ContentManager
                .GetContentTypeDefinitions()
                .Where(contentTypeDefinition => contentTypeDefinition.Settings.ContainsKey("Stereotype") && contentTypeDefinition.Settings["Stereotype"] == "Media")
                .OrderBy(x => x.DisplayName)
                .ToArray();
        }

        public IContentQuery<MediaPart, MediaPartRecord> GetMediaContentItems() {
            return _orchardServices.ContentManager.Query<MediaPart, MediaPartRecord>();
        }

        public IEnumerable<MediaPart> GetMediaContentItems(string folderPath, int skip, int count, string order, string mediaType) {
            var query = _orchardServices.ContentManager.Query<MediaPart>();

            if (!String.IsNullOrEmpty(mediaType)) {
                query = query.ForType(new[] { mediaType });
            }

            if (!String.IsNullOrEmpty(folderPath)) {
                query = query.Join<MediaPartRecord>().Where(m => m.FolderPath == folderPath);
            }

            switch(order) {
                case "title":
                    return query.Join<TitlePartRecord>()
                                    .OrderBy(x => x.Title)
                                    .Slice(skip, count)
                                    .ToArray();

                case "modified":
                    return query.Join<CommonPartRecord>()
                                    .OrderByDescending(x => x.ModifiedUtc)
                                    .Slice(skip, count)
                                    .ToArray();

                case "published":
                    return query.Join<CommonPartRecord>()
                                    .OrderByDescending(x => x.PublishedUtc)
                                    .Slice(skip, count)
                                    .ToArray();

                default:
                    return query.Join<CommonPartRecord>()
                                    .OrderByDescending(x => x.CreatedUtc)
                                    .Slice(skip, count)
                                    .ToArray();
            }
        }

        public IEnumerable<MediaPart> GetMediaContentItems(int skip, int count, string order, string mediaType) {
            return GetMediaContentItems(null, skip, count, order, mediaType);
        }

        public int GetMediaContentItemsCount(string folderPath, string mediaType) {
            var query = _orchardServices.ContentManager.Query<MediaPart>();

            if (!String.IsNullOrEmpty(mediaType)) {
                query = query.ForType(new[] { mediaType });
            }

            if (!String.IsNullOrEmpty(folderPath)) {
                query = query.Join<MediaPartRecord>().Where(m => m.FolderPath == folderPath);
            }

            return query.Count();
        }

        public int GetMediaContentItemsCount(string mediaType) {
            return GetMediaContentItemsCount(null, mediaType);
        }

        public MediaPart ImportMedia(Stream stream, string relativePath, string filename) {
            return ImportMedia(stream, relativePath, filename, null);
        }

        public MediaPart ImportMedia(Stream stream, string relativePath, string filename, string contentType) {
            var uniqueFilename = GetUniqueFilename(relativePath, filename);

            UploadMediaFile(relativePath, uniqueFilename, stream);
            return ImportMedia(relativePath, uniqueFilename, contentType);
        }

        public string GetUniqueFilename(string folderPath, string filename) {
            // compute a unique filename
            var uniqueFilename = filename;
            var index = 1;
            while (_storageProvider.FileExists(_storageProvider.Combine(folderPath, uniqueFilename))) {
                uniqueFilename = Path.GetFileNameWithoutExtension(filename) + "-" + index++ + Path.GetExtension(filename);
            }

            return uniqueFilename;
        }

        public MediaPart ImportMedia(string relativePath, string filename) {
            return ImportMedia(relativePath, filename, null);
        }

        public MediaPart ImportMedia(string relativePath, string filename, string contentType) {

            var mimeType = _mimeTypeProvider.GetMimeType(filename);

            if (!_storageProvider.FileExists(_storageProvider.Combine(relativePath, filename))) {
                return null;
            }

            var storageFile = _storageProvider.GetFile(_storageProvider.Combine(relativePath, filename));
            var mediaFile = BuildMediaFile(relativePath, storageFile);

            using (var stream = storageFile.OpenRead()) {
                var mediaFactory = GetMediaFactory(stream, mimeType, contentType);
                var mediaPart = mediaFactory.CreateMedia(stream, mediaFile.Name, mimeType, contentType);
                if (mediaPart != null) {
                    mediaPart.FolderPath = relativePath;
                    mediaPart.FileName = filename;
                }

                return mediaPart;
            }
        }

        public IMediaFactory GetMediaFactory(Stream stream, string mimeType, string contentType) {
            var requestMediaFactoryResults = _mediaFactorySelectors
                .Select(x => x.GetMediaFactory(stream, mimeType, contentType))
                .Where(x => x != null)
                .OrderByDescending(x => x.Priority);

            if (!requestMediaFactoryResults.Any() )
                return null;

            return requestMediaFactoryResults.First().MediaFactory;
        }

        /// <summary>
        /// Retrieves the public path based on the relative path within the media directory.
        /// </summary>
        /// <example>
        /// "/Media/Default/InnerDirectory/Test.txt" based on the input "InnerDirectory/Test.txt"
        /// </example>
        /// <param name="relativePath">The relative path within the media directory.</param>
        /// <returns>The public path relative to the application url.</returns>
        public string GetPublicUrl(string relativePath) {
            Argument.ThrowIfNullOrEmpty(relativePath, "relativePath");

            return _storageProvider.GetPublicUrl(relativePath);
        }

        /// <summary>
        /// Returns the public URL for a media file.
        /// </summary>
        /// <param name="mediaPath">The relative path of the media folder containing the media.</param>
        /// <param name="fileName">The media file name.</param>
        /// <returns>The public URL for the media.</returns>
        public string GetMediaPublicUrl(string mediaPath, string fileName) {
            return GetPublicUrl(Path.Combine(mediaPath, fileName));
        }

        /// <summary>
        /// Retrieves the media folders within a given relative path.
        /// </summary>
        /// <param name="relativePath">The path where to retrieve the media folder from. null means root.</param>
        /// <returns>The media folder in the given path.</returns>
        public IEnumerable<MediaFolder> GetMediaFolders(string relativePath) {
            return _storageProvider
                .ListFolders(relativePath)
                .Select(BuildMediaFolder)
                .Where(f => !f.Name.Equals("RecipeJournal", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Name.StartsWith("_"))
                .ToList();
        }

        private static MediaFolder BuildMediaFolder(IStorageFolder folder) {
            return new MediaFolder {
                Name = folder.GetName(),
                Size = folder.GetSize(),
                LastUpdated = folder.GetLastUpdated(),
                MediaPath = folder.GetPath()
            };
        }

        /// <summary>
        /// Retrieves the media files within a given relative path.
        /// </summary>
        /// <param name="relativePath">The path where to retrieve the media files from. null means root.</param>
        /// <returns>The media files in the given path.</returns>
        public IEnumerable<MediaFile> GetMediaFiles(string relativePath) {
            return _storageProvider.ListFiles(relativePath).Select(file =>
                BuildMediaFile(relativePath, file)).ToList();
        }

        private MediaFile BuildMediaFile(string relativePath, IStorageFile file) {
            return new MediaFile {
                Name = file.GetName(),
                Size = file.GetSize(),
                LastUpdated = file.GetLastUpdated(),
                Type = file.GetFileType(),
                FolderName = relativePath,
                MediaPath = GetMediaPublicUrl(relativePath, file.GetName())
            };
        }

        /// <summary>
        /// Creates a media folder.
        /// </summary>
        /// <param name="relativePath">The path where to create the new folder. null means root.</param>
        /// <param name="folderName">The name of the folder to be created.</param>
        public void CreateFolder(string relativePath, string folderName) {
            Argument.ThrowIfNullOrEmpty(folderName, "folderName");

            _storageProvider.CreateFolder(relativePath == null ? folderName : _storageProvider.Combine(relativePath, folderName));
        }

        /// <summary>
        /// Deletes a media folder.
        /// </summary>
        /// <param name="folderPath">The path to the folder to be deleted.</param>
        public void DeleteFolder(string folderPath) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");

            _storageProvider.DeleteFolder(folderPath);
        }

        /// <summary>
        /// Renames a media folder.
        /// </summary>
        /// <param name="folderPath">The path to the folder to be renamed.</param>
        /// <param name="newFolderName">The new folder name.</param>
        public void RenameFolder(string folderPath, string newFolderName) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNullOrEmpty(newFolderName, "newFolderName");

            _storageProvider.RenameFolder(folderPath, _storageProvider.Combine(Path.GetDirectoryName(folderPath), newFolderName));
        }

        /// <summary>
        /// Deletes a media file.
        /// </summary>
        /// <param name="folderPath">The folder path.</param>
        /// <param name="fileName">The file name.</param>
        public void DeleteFile(string folderPath, string fileName) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNullOrEmpty(fileName, "fileName");

            _storageProvider.DeleteFile(_storageProvider.Combine(folderPath, fileName));
        }

        /// <summary>
        /// Renames a media file.
        /// </summary>
        /// <param name="folderPath">The path to the file's parent folder.</param>
        /// <param name="currentFileName">The current file name.</param>
        /// <param name="newFileName">The new file name.</param>
        public void RenameFile(string folderPath, string currentFileName, string newFileName) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNullOrEmpty(currentFileName, "currentFileName");
            Argument.ThrowIfNullOrEmpty(newFileName, "newFileName");

            _storageProvider.RenameFile(_storageProvider.Combine(folderPath, currentFileName), _storageProvider.Combine(folderPath, newFileName));
        }

        /// <summary>
        /// Moves a media file.
        /// </summary>
        /// <param name="currentPath">The path to the file's parent folder.</param>
        /// <param name="filename">The file name.</param>
        /// <param name="newPath">The path where the file will be moved to.</param>
        /// <param name="newFilename">The new file name.</param>
        public void MoveFile(string currentPath, string filename, string newPath, string newFilename) {
            Argument.ThrowIfNullOrEmpty(currentPath, "currentPath");
            Argument.ThrowIfNullOrEmpty(newPath, "newPath");
            Argument.ThrowIfNullOrEmpty(filename, "filename");
            Argument.ThrowIfNullOrEmpty(newFilename, "newFilename");

            _storageProvider.RenameFile(_storageProvider.Combine(currentPath, filename), _storageProvider.Combine(newPath, newFilename));
        }

        /// <summary>
        /// Uploads a media file based on a posted file.
        /// </summary>
        /// <param name="folderPath">The path to the folder where to upload the file.</param>
        /// <param name="postedFile">The file to upload.</param>
        /// <returns>The path to the uploaded file.</returns>
        public string UploadMediaFile(string folderPath, HttpPostedFileBase postedFile) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNull(postedFile, "postedFile");

            return UploadMediaFile(folderPath, Path.GetFileName(postedFile.FileName), postedFile.InputStream);
        }

        /// <summary>
        /// Uploads a media file based on an array of bytes.
        /// </summary>
        /// <param name="folderPath">The path to the folder where to upload the file.</param>
        /// <param name="fileName">The file name.</param>
        /// <param name="bytes">The array of bytes with the file's contents.</param>
        /// <returns>The path to the uploaded file.</returns>
        public string UploadMediaFile(string folderPath, string fileName, byte[] bytes) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNullOrEmpty(fileName, "fileName");
            Argument.ThrowIfNull(bytes, "bytes");

            return UploadMediaFile(folderPath, fileName, new MemoryStream(bytes));
        }

        /// <summary>
        /// Uploads a media file based on a stream.
        /// </summary>
        /// <param name="folderPath">The folder path to where to upload the file.</param>
        /// <param name="fileName">The file name.</param>
        /// <param name="inputStream">The stream with the file's contents.</param>
        /// <returns>The path to the uploaded file.</returns>
        public string UploadMediaFile(string folderPath, string fileName, Stream inputStream) {
            Argument.ThrowIfNullOrEmpty(folderPath, "folderPath");
            Argument.ThrowIfNullOrEmpty(fileName, "fileName");
            Argument.ThrowIfNull(inputStream, "inputStream");

            string filePath = _storageProvider.Combine(folderPath, fileName);
            _storageProvider.SaveStream(filePath, inputStream);

            return _storageProvider.GetPublicUrl(filePath);
        }
    }
}