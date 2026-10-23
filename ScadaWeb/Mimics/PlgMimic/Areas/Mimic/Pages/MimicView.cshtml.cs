// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Mvc.RazorPages;
using Scada.Data.Entities;
using Scada.Web.Lang;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimic.Areas.Mimic.Pages
{
    /// <summary>
    /// Represents a mimic view page.
    /// <para>Представляет страницу представления мнемосхемы.</para>
    /// </summary>
    public class MimicViewModel(
        IWebContext webContext, 
        IUserContext userContext) : PageModel
    {
        public int ViewID { get; private set; } = 0;
        public bool ControlRight { get; private set; } = false;

        /// <summary>
        /// Gets whether the current user has full rights. Used by components for role filtering.
        /// </summary>
        public bool IsAdmin { get; private set; } = false;

        /// <summary>
        /// Gets whether the current user may open this view in the mimic editor.
        /// </summary>
        public bool CanEditMimic { get; private set; } = false;

        /// <summary>
        /// Gets the URL that opens this view in the mimic editor.
        /// </summary>
        public string EditMimicUrl { get; private set; } = "";
        public List<View> NavigationViews { get; private set; } = [];


        public void OnGet(int? id)
        {
            ViewID = id ?? userContext.Views.GetFirstViewID() ?? 0;
            View viewEntity = webContext.ConfigDatabase.ViewTable.GetItem(ViewID);
            NavigationViews = webContext.ConfigDatabase.SortedViews
                .Where(v => !v.Hidden && !string.IsNullOrEmpty(v.Path) && !v.Path.EndsWith('\\') && !v.Path.EndsWith('/') &&
                    userContext.Rights.GetRightByView(v).View).ToList();
            ControlRight = webContext.AppConfig.GeneralOptions.EnableCommands &&
                userContext.Rights.GetRightByView(viewEntity).Control;
            ViewData["Title"] = string.Format(WebPhrases.ViewTitle, ViewID);
            IsAdmin = userContext.Rights.Full;

            // Every mimic view gets an editor entry for administrators. The editor resolves
            // relative paths against its working directory, so pass an absolute file name.
            CanEditMimic = userContext.Rights.Full && viewEntity != null &&
                viewEntity.Path.EndsWith(".mim", StringComparison.OrdinalIgnoreCase);

            if (CanEditMimic)
            {
                string instanceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                string viewFile = Path.Combine(instanceDir, "Views", viewEntity.Path);
                EditMimicUrl = "~/MimicEditor/MimicOpen?fileName=" + Uri.EscapeDataString(viewFile);
            }
        }
    }
}
