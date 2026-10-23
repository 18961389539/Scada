// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Scada.Data.Entities;
using Scada.Data.Const;
using System.Text.Json;
using Scada.Web.Authorization;
using Scada.Web.Plugins.PlgMimicEditor.Code;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimicEditor.Areas.MimicEditor.Pages
{
    /// <summary>
    /// Represents a main page of the mimic editor.
    /// <para>Представляет основную страницу редактора мнемосхем.</para>
    /// </summary>
    [Authorize(Policy = PolicyName.Administrators)]
    public class MimicEditModel(EditorManager editorManager, IWebContext webContext) : PageModel
    {
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public string ErrorMessage { get; private set; } = "";
        public long MimicKey { get; private set; } = 0;

        /// <summary>
        /// Gets the URL of the runtime view that displays the mimic being edited.
        /// </summary>
        public string RuntimeViewUrl { get; private set; } = "";
        public string ChannelCatalogJson { get; private set; } = "[]";

        /// <summary>
        /// Gets whether an auto-saved draft exists and when it was written.
        /// </summary>
        public bool HasAutoSave { get; private set; }
        public string AutoSaveTime { get; private set; } = "";


        public void OnGet(long key)
        {
            LoadMimicState(key);
        }

        /// <summary>
        /// Restores the auto-saved draft: the original is backed up, the draft replaces it,
        /// and the mimic is reopened so the editor shows the restored content.
        /// </summary>
        public IActionResult OnPostRestoreAutoSave(long key)
        {
            if (editorManager.RestoreAutoSave(key, out long newKey, out string errMsg))
                return RedirectToPage("MimicEdit", new { key = newKey });

            LoadMimicState(key);
            ErrorMessage = errMsg;
            return Page();
        }

        /// <summary>
        /// Throws the auto-saved draft away.
        /// </summary>
        public IActionResult OnPostDiscardAutoSave(long key)
        {
            editorManager.DiscardAutoSave(key);
            return RedirectToPage("MimicEdit", new { key });
        }

        /// <summary>
        /// Fills the page with the mimic being edited and its auto-save state.
        /// </summary>
        private void LoadMimicState(long key)
        {
            MimicKey = key;

            if (editorManager.FindMimic(key, out MimicInstance mimicInstance, out string errMsg))
            {
                ViewData["Title"] = Path.GetFileName(mimicInstance.FileName);
                RuntimeViewUrl = FindRuntimeViewUrl(mimicInstance.FileName);
                ChannelCatalogJson = JsonSerializer.Serialize(webContext.ConfigDatabase.CnlTable.Items.Values
                    .Where(c => c.Active).OrderBy(c => c.CnlNum).Select(c => new
                    {
                        number = c.CnlNum,
                        name = c.Name,
                        unit = c.UnitID.HasValue ? webContext.ConfigDatabase.UnitTable.GetItem(c.UnitID.Value)?.Name ?? "" : "",
                        readable = CnlTypeID.IsArchivable(c.CnlTypeID),
                        writable = CnlTypeID.IsOutput(c.CnlTypeID)
                    }));

                if (editorManager.HasAutoSave(key, out DateTime autoSaveTime))
                {
                    HasAutoSave = true;
                    AutoSaveTime = autoSaveTime.ToString("MM-dd HH:mm:ss");
                }
            }
            else
            {
                ViewData["Title"] = key.ToString();
                ErrorMessage = errMsg;
            }
        }

        /// <summary>
        /// Finds the view that refers to the specified mimic file.
        /// </summary>
        private string FindRuntimeViewUrl(string fileName)
        {
            try
            {
                string instanceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                string viewsDir = Path.GetFullPath(Path.Combine(instanceDir, "Views"));
                string fullName = Path.GetFullPath(fileName);

                if (!fullName.StartsWith(viewsDir, StringComparison.OrdinalIgnoreCase))
                    return "";

                string relativePath = fullName[viewsDir.Length..].TrimStart('\\', '/').Replace('/', '\\');

                foreach (View view in webContext.ConfigDatabase.ViewTable.Items.Values)
                {
                    if (string.Equals(view.Path, relativePath, StringComparison.OrdinalIgnoreCase))
                        return Url.Content("~/View/" + view.ViewID);
                }
            }
            catch
            {
                // The editor still works without the reverse link.
            }

            return "";
        }
    }
}
