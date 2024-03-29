﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using Orchard.Data.Migration;
using Orchard.DisplayManagement;
using Orchard.Environment.Descriptor.Models;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Models;
using Orchard.Environment.Features;
using Orchard.Localization;
using Orchard.Logging;
using Orchard.Modules.Events;
using Orchard.Modules.Models;
using Orchard.Modules.Services;
using Orchard.Modules.ViewModels;
using Orchard.Mvc;
using Orchard.Mvc.Extensions;
using Orchard.Recipes.Models;
using Orchard.Recipes.Services;
using Orchard.Reports.Services;
using Orchard.Security;
using Orchard.UI.Navigation;
using Orchard.UI.Notify;

namespace Orchard.Modules.Controllers {
    public class AdminController : Controller {
        private readonly IExtensionDisplayEventHandler _extensionDisplayEventHandler;
        private readonly IModuleService _moduleService;
        private readonly IDataMigrationManager _dataMigrationManager;
        private readonly IReportsCoordinator _reportsCoordinator;
        private readonly IExtensionManager _extensionManager;
        private readonly IFeatureManager _featureManager;
        private readonly IRecipeHarvester _recipeHarvester;
        private readonly IRecipeManager _recipeManager;
        private readonly ShellDescriptor _shellDescriptor;

        public AdminController(
            IEnumerable<IExtensionDisplayEventHandler> extensionDisplayEventHandlers,
            IOrchardServices services,
            IModuleService moduleService,
            IDataMigrationManager dataMigrationManager,
            IReportsCoordinator reportsCoordinator,
            IExtensionManager extensionManager,
            IFeatureManager featureManager,
            IRecipeHarvester recipeHarvester,
            IRecipeManager recipeManager,
            ShellDescriptor shellDescriptor,
            IShapeFactory shapeFactory)
        {
            Services = services;
            _extensionDisplayEventHandler = extensionDisplayEventHandlers.FirstOrDefault();
            _moduleService = moduleService;
            _dataMigrationManager = dataMigrationManager;
            _reportsCoordinator = reportsCoordinator;
            _extensionManager = extensionManager;
            _featureManager = featureManager;
            _recipeHarvester = recipeHarvester;
            _recipeManager = recipeManager;
            _shellDescriptor = shellDescriptor;
            Shape = shapeFactory;

            T = NullLocalizer.Instance;
            Logger = NullLogger.Instance;
        }

        public Localizer T { get; set; }
        public IOrchardServices Services { get; set; }
        public ILogger Logger { get; set; }
        public dynamic Shape { get; set; }

        public ActionResult Index(ModulesIndexOptions options, PagerParameters pagerParameters) {
            if (!Services.Authorizer.Authorize(StandardPermissions.SiteOwner, T("Not allowed to manage modules")))
                return new HttpUnauthorizedResult();

            Pager pager = new Pager(Services.WorkContext.CurrentSite, pagerParameters);

            IEnumerable<ModuleEntry> modules = _extensionManager.AvailableExtensions()
                .Where(extensionDescriptor => DefaultExtensionTypes.IsModule(extensionDescriptor.ExtensionType) &&
                                              (string.IsNullOrEmpty(options.SearchText) || extensionDescriptor.Name.ToLowerInvariant().Contains(options.SearchText.ToLowerInvariant())))
                .OrderBy(extensionDescriptor => extensionDescriptor.Name)
                .Select(extensionDescriptor => new ModuleEntry { Descriptor = extensionDescriptor });

            int totalItemCount = modules.Count();

            if (pager.PageSize != 0) {
                modules = modules.Skip((pager.Page - 1) * pager.PageSize).Take(pager.PageSize);
            }

            modules = modules.ToList();
            foreach (ModuleEntry moduleEntry in modules) {
                moduleEntry.IsRecentlyInstalled = _moduleService.IsRecentlyInstalled(moduleEntry.Descriptor);

                if (_extensionDisplayEventHandler != null) {
                    foreach (string notification in _extensionDisplayEventHandler.Displaying(moduleEntry.Descriptor, ControllerContext.RequestContext)) {
                        moduleEntry.Notifications.Add(notification);
                    }
                }
            }

            return View(new ModulesIndexViewModel {
                Modules = modules,
                InstallModules = _featureManager.GetEnabledFeatures().FirstOrDefault(f => f.Id == "PackagingServices") != null,
                Options = options,
                Pager = Shape.Pager(pager).TotalItemCount(totalItemCount)
            });
        }

        public ActionResult Recipes() {
            if (!Services.Authorizer.Authorize(StandardPermissions.SiteOwner, T("Not allowed to manage modules")))
                return new HttpUnauthorizedResult();

            IEnumerable<ModuleEntry> modules = _extensionManager.AvailableExtensions()
                .Where(extensionDescriptor => DefaultExtensionTypes.IsModule(extensionDescriptor.ExtensionType))
                .Where(extensionDescriptor => extensionDescriptor.Id != "Orchard.Setup")
                .OrderBy(extensionDescriptor => extensionDescriptor.Name)
                .Select(extensionDescriptor => new ModuleEntry { Descriptor = extensionDescriptor });

            var viewModel = new RecipesViewModel();

            if (_recipeHarvester != null) {
                viewModel.Modules = modules.Select(x => new ModuleRecipesViewModel {
                    Module = x,
                    Recipes = _recipeHarvester.HarvestRecipes(x.Descriptor.Id).ToList()
                })
                .Where(x => x.Recipes.Any());
            }

            return View(viewModel);

        }

        [HttpPost, ActionName("Recipes")]
        public ActionResult RecipesPOST(string moduleId, string name) {
            if (!Services.Authorizer.Authorize(StandardPermissions.SiteOwner, T("Not allowed to manage modules")))
                return new HttpUnauthorizedResult();

            ModuleEntry module = _extensionManager.AvailableExtensions()
                .Where(extensionDescriptor => extensionDescriptor.Id == moduleId)
                .Select(extensionDescriptor => new ModuleEntry { Descriptor = extensionDescriptor }).FirstOrDefault();

            if (module == null) {
                return HttpNotFound();
            }

            Recipe recipe = _recipeHarvester.HarvestRecipes(module.Descriptor.Id).FirstOrDefault(x => x.Name == name);

            if (recipe == null) {
                return HttpNotFound();
            }

            try {
                _recipeManager.Execute(recipe);
            }
            catch(Exception e) {
                Logger.Error(e, "Error while executing recipe {0} in {1}", moduleId, name);
                Services.Notifier.Error(T("Recipes contains {0} unsupported module installation steps.", recipe.Name));
            }

            Services.Notifier.Information(T("The recipe {0} was executed successfully.", recipe.Name));
            
            return RedirectToAction("Recipes");

        }
        public ActionResult Features() {
            if (!Services.Authorizer.Authorize(Permissions.ManageFeatures, T("Not allowed to manage features")))
                return new HttpUnauthorizedResult();

            var featuresThatNeedUpdate = _dataMigrationManager.GetFeaturesThatNeedUpdate();

            IEnumerable<ModuleFeature> features = _featureManager.GetAvailableFeatures()
                .Where(f => !DefaultExtensionTypes.IsTheme(f.Extension.ExtensionType))
                .Select(f => new ModuleFeature {
                                Descriptor = f,
                                IsEnabled = _shellDescriptor.Features.Any(sf => sf.Name == f.Id),
                                IsRecentlyInstalled = _moduleService.IsRecentlyInstalled(f.Extension),
                                NeedsUpdate = featuresThatNeedUpdate.Contains(f.Id)
                            });

            return View(new FeaturesViewModel { Features = features });
        }

        [HttpPost, ActionName("Features")]
        [FormValueRequired("submit.BulkExecute")]
        public ActionResult FeaturesPOST(FeaturesBulkAction bulkAction, IList<string> featureIds, bool? force) {

            if (!Services.Authorizer.Authorize(Permissions.ManageFeatures, T("Not allowed to manage features")))
                return new HttpUnauthorizedResult();

            if (featureIds == null || !featureIds.Any()) {
                ModelState.AddModelError("featureIds", T("Please select one or more features."));
            }

            if (ModelState.IsValid) {
                var availableFeatures = _moduleService.GetAvailableFeatures().ToList();
                var selectedFeatures = availableFeatures.Where(x => featureIds.Contains(x.Descriptor.Id)).ToList();
                var enabledFeatures = availableFeatures.Where(x => x.IsEnabled && featureIds.Contains(x.Descriptor.Id)).Select(x => x.Descriptor.Id).ToList();
                var disabledFeatures = availableFeatures.Where(x => !x.IsEnabled && featureIds.Contains(x.Descriptor.Id)).Select(x => x.Descriptor.Id).ToList();

                switch (bulkAction) {
                    case FeaturesBulkAction.None:
                        break;
                    case FeaturesBulkAction.Enable:
                        _moduleService.EnableFeatures(disabledFeatures, force == true);
                        break;
                    case FeaturesBulkAction.Disable:
                        _moduleService.DisableFeatures(enabledFeatures, force == true);
                        break;
                    case FeaturesBulkAction.Toggle:
                        _moduleService.EnableFeatures(disabledFeatures, force == true);
                        _moduleService.DisableFeatures(enabledFeatures, force == true);
                        break;
                    case FeaturesBulkAction.Update:
                        foreach (var feature in selectedFeatures.Where(x => x.NeedsUpdate)) {
                            var id = feature.Descriptor.Id;
                            try {
                                _reportsCoordinator.Register("Data Migration", "Upgrade " + id, "Orchard installation");
                                _dataMigrationManager.Update(id);
                                Services.Notifier.Information(T("The feature {0} was updated successfully", id));
                            }
                            catch (Exception exception) {
                                Services.Notifier.Error(T("An error occured while updating the feature {0}: {1}", id, exception.Message));
                            }
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return RedirectToAction("Features");
        }
    }
}