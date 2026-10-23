// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Globalization;
using System.Xml.Linq;
using Scada.Data.Const;

namespace Scada.Web.Plugins.PlgMimicEditor.Code
{
    /// <summary>
    /// Represents a configuration problem found in a mimic file.
    /// </summary>
    public record MimicIssue(string Message, bool IsError, int? ComponentID = null,
        string ComponentName = "", string ComponentType = "", string PropertyName = "");

    /// <summary>
    /// Inspects stored mimic settings without loading plugins or contacting a device.
    /// </summary>
    public static class MimicInspector
    {
        private static readonly HashSet<string> ReadChannelProperties = new(StringComparer.Ordinal)
        {
            "InCnlNum", "Channels", "FeedbackChannel", "ProcessChannel", "StatusChannel", "FaultChannel",
            "TripChannel", "EnergizedChannel", "ModeChannel", "PermitChannel", "OutputChannel"
        };

        private static readonly HashSet<string> WriteChannelProperties = new(StringComparer.Ordinal)
        {
            "OutCnlNum", "SetpointChannel", "ModeOutputChannel"
        };

        /// <summary>
        /// Checks the component tree using the current configured channel types.
        /// The lookup returns null when the channel does not exist.
        /// </summary>
        public static List<MimicIssue> Inspect(XDocument document, Func<int, int?> getChannelType)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(getChannelType);
            List<MimicIssue> issues = [];
            XElement root = document.Root;

            if (root == null)
            {
                issues.Add(new MimicIssue("画面内容为空", true));
                return issues;
            }

            if (root.Element("Document")?.Element("Size") is XElement sizeElem)
            {
                string widthText = sizeElem.Element("Width")?.Value ?? "";
                string heightText = sizeElem.Element("Height")?.Value ?? "";

                if (!TryParseInteger(widthText, out int width) || !TryParseInteger(heightText, out int height) ||
                    width <= 0 || height <= 0 || width > 20000 || height > 20000)
                {
                    issues.Add(new MimicIssue($"画面尺寸异常：{widthText} × {heightText}", true,
                        PropertyName: "Document.Size"));
                }
            }

            foreach (XElement componentElem in EnumerateComponents(root.Element("Components")))
                InspectComponent(componentElem, getChannelType, issues);

            return issues;
        }

        /// <summary>
        /// Follows only direct Components collections, as the mimic loader does.
        /// Property objects such as Font, Size and PropertyBindings are not components.
        /// </summary>
        private static IEnumerable<XElement> EnumerateComponents(XElement componentsElem)
        {
            if (componentsElem == null)
                yield break;

            Stack<XElement> pending = new(componentsElem.Elements().Reverse());

            while (pending.TryPop(out XElement componentElem))
            {
                yield return componentElem;

                if (componentElem.Element("Components") is XElement childrenElem)
                {
                    foreach (XElement childElem in childrenElem.Elements().Reverse())
                        pending.Push(childElem);
                }
            }
        }

        private static void InspectComponent(XElement componentElem, Func<int, int?> getChannelType,
            List<MimicIssue> issues)
        {
            int? componentID = TryParseInteger(componentElem.Element("ID")?.Value, out int parsedID) && parsedID > 0
                ? parsedID : null;
            string componentName = componentElem.Element("Name")?.Value ?? "";
            string componentType = componentElem.Name.LocalName;
            string description = string.IsNullOrWhiteSpace(componentName) ? componentType
                : $"{componentName}（{componentType}）";

            if (componentID.HasValue)
                description += $" #{componentID}";

            bool hasInput = false;

            void AddIssue(string message, bool isError, string propertyName) =>
                issues.Add(new MimicIssue($"{description}：{message}", isError, componentID,
                    componentName, componentType, propertyName));

            foreach (XElement propertyElem in componentElem.Elements())
            {
                string propertyName = propertyElem.Name.LocalName;
                bool isRead = ReadChannelProperties.Contains(propertyName);
                bool isWrite = WriteChannelProperties.Contains(propertyName);

                if (!isRead && !isWrite)
                    continue;

                string rawValue = propertyElem.Value.Trim();

                if (rawValue.Length == 0)
                    continue;

                if (!TryParseChannelNumbers(rawValue, propertyName == "Channels", out List<int> cnlNums))
                {
                    AddIssue($"{propertyName} 的值 \"{rawValue}\" 不是有效的通道号", true, propertyName);
                    continue;
                }

                foreach (int cnlNum in cnlNums.Distinct())
                {
                    if (cnlNum <= 0)
                        continue;

                    int? cnlType = getChannelType(cnlNum);

                    if (!cnlType.HasValue)
                        AddIssue($"{propertyName} 引用的通道 {cnlNum} 不存在", true, propertyName);
                    else if (isWrite && !CnlTypeID.IsOutput(cnlType))
                        AddIssue($"{propertyName} 引用的通道 {cnlNum} 为只读，无法下发命令", true, propertyName);
                    else if (isRead && !CnlTypeID.IsArchivable(cnlType))
                        AddIssue($"{propertyName} 引用的通道 {cnlNum} 无可读取的数值", true, propertyName);
                    else if (isRead)
                        hasInput = true;
                }
            }

            if (hasInput && componentElem.Element("Label") is XElement labelElem &&
                string.IsNullOrWhiteSpace(labelElem.Value))
            {
                AddIssue("绑定了通道但标签为空，运行页看不出含义", false, "Label");
            }
        }

        private static bool TryParseInteger(string value, out int number) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

        private static bool TryParseChannelNumbers(string value, bool isList, out List<int> cnlNums)
        {
            cnlNums = [];

            if (!isList)
            {
                if (!TryParseInteger(value, out int cnlNum))
                    return false;

                cnlNums.Add(cnlNum);
                return true;
            }

            foreach (string part in value.Split([',', ';', ' ', '，', '；', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseInteger(part, out int cnlNum))
                    return false;

                cnlNums.Add(cnlNum);
            }

            return true;
        }
    }
}
