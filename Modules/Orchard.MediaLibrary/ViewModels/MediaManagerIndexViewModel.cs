﻿using System.Collections.Generic;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.MediaLibrary.Models;

namespace Orchard.MediaLibrary.ViewModels {
    public class MediaManagerIndexViewModel {
        public IEnumerable<FolderHierarchy> Folders { get; set; }
        public string FolderPath { get; set; }
        public bool DialogMode { get; set; }
        public IEnumerable<ContentTypeDefinition> MediaTypes { get; set; }
        public dynamic CustomActionsShapes { get; set; }
        public dynamic CustomNavigationShapes { get; set; }
    }
}