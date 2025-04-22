using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Log73;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Class definitions
class Config
{
    public string YamlPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string IndexSlug { get; set; } = "/api";
    public ConfigTypesGrouping? TypesGrouping { get; set; }
    public string BrNewline { get; set; } = "\n\n";
    public bool ForceNewline { get; set; } = false;
    public string ForcedNewline { get; set; } = "  \n";
    public bool RewriteInterlinks { get; set; } = false;
}

public class ConfigTypesGrouping
{
    public bool Enabled { get; set; }
    public int MinCount { get; set; } = 12;
}

class DocFxFile
{
    public Item[] Items { get; set; } = Array.Empty<Item>();
}

class Item
{
    public string Uid { get; set; } = string.Empty;
    public string CommentId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string[] Children { get; set; } = Array.Empty<string>();
    public string[] Langs { get; set; } = Array.Empty<string>();
    public string Definition { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameWithType { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Source? Source { get; set; }
    public string[] Assemblies { get; set; } = Array.Empty<string>();
    public string Namespace { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public Syntax? Syntax { get; set; }
    public string[]? Inheritance { get; set; }
    public string[]? DerivedClasses { get; set; }
    public string[]? Implements { get; set; }
    public string[]? ExtensionMethods { get; set; }
    public ThrowsException[]? Exceptions { get; set; }
}

class ThrowsException
{
    public string Type { get; set; } = string.Empty;
    public string CommentId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

class Syntax
{
    public string Content { get; set; } = string.Empty;
    [YamlMember(Alias = "content.vb")]
    public string ContentVb { get; set; } = string.Empty;
    public Parameter[]? Parameters { get; set; }
    public TypeParameter[]? TypeParameters { get; set; }
    public SyntaxReturn? Return { get; set; }
}

class Source
{
    public Remote? Remote { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StartLine { get; set; }
}

class Remote
{
    public string Path { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
}

class Parameter
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
}

class TypeParameter
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

class SyntaxReturn
{
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
}

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618

class Program
{
    static async Task Main()
    {
        if (Environment.GetEnvironmentVariable("JAN_DEBUG") == "1")
            Log73.Console.Options.LogLevel = LogLevel.Debug;

        var versionString = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        Log73.Console.WriteLine($"DocFxMarkdownGen v{versionString} running...");

        var xrefRegex = new Regex("<xref href=\"(.+?)\" data-throw-if-not-resolved=\"false\"></xref>", RegexOptions.Compiled);
        var langwordXrefRegex =
            new Regex("<xref uid=\"langword_csharp_.+?\" name=\"(.+?)\" href=\"\"></xref>", RegexOptions.Compiled);
        var codeBlockRegex = new Regex("<pre><code class=\"lang-csharp\">((.|\n)+?)</code></pre>", RegexOptions.Compiled);
        var codeRegex = new Regex("<code>(.+?)</code>", RegexOptions.Compiled);
        var linkRegex = new Regex("<a href=\"(.+?)\">(.+?)</a>", RegexOptions.Compiled);
        var brRegex = new Regex("<br */?>", RegexOptions.Compiled);
        var yamlDeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var config =
            yamlDeserializer.Deserialize<Config>(
                await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("DFMG_CONFIG") ?? "./config.yaml"));
        if (Environment.GetEnvironmentVariable("DFMG_OUTPUT_PATH") is { } outputPath and not "")
        {
            config.OutputPath = outputPath;
            Log73.Console.Info($"Output path overriden by env: {config.OutputPath}");
        }

        if (Environment.GetEnvironmentVariable("DFMG_YAML_PATH") is { } yamlPath and not "")
        {
            config.YamlPath = yamlPath;
            Log73.Console.Info($"YAML path overriden by env: {config.YamlPath}");
        }

        if (Directory.Exists(config.OutputPath))
            Directory.Delete(config.OutputPath, true);
        Directory.CreateDirectory(config.OutputPath);

        var stopwatch = Stopwatch.StartNew();
        List<Item> items = new();

        #region read all yaml and create directory structure

        await Parallel.ForEachAsync(Directory.GetFiles(config.YamlPath, "*.yml"), async (file, _) =>
        {
            if (file.EndsWith("toc.yml"))
                return;
            Log73.Console.Debug(file);
            var obj = yamlDeserializer.Deserialize<DocFxFile>(await File.ReadAllTextAsync(file));
            lock (items)
            {
                items.AddRange(obj.Items);
            }
        });
        Log73.Console.Info($"Read all YAML in {stopwatch.ElapsedMilliseconds}ms.");
        // create namespace directories
        Parallel.ForEach(items, (item, _) =>
        {
            if (item.Type == "Namespace")
            {
                Log73.Console.Debug(item.Type + ": " + item.Name);
                var dir = Path.Combine(config.OutputPath, item.Name);
                Directory.CreateDirectory(dir);
            }
        });

        #endregion

        // util methods
        static string GetTypePathPart(string type)
            => type switch
            {
                "Class" => "Classes",
                "Struct" => "Structs",
                "Interface" => "Interfaces",
                "Enum" => "Enums",
                "Delegate" => "Delegates",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

        Item[] GetProperties(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Property").ToArray();

        Item[] GetFields(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Field").ToArray();

        Item[] GetMethods(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Method").ToArray();

        Item[] GetEvents(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Event").ToArray();

        string? HtmlEscape(string? str)
        {
            if (str == null) return null;
            return str.Replace("&", "&amp;"); // Only escape ampersands, no need to escape quotes here
        }

        string SafeFrontmatterValue(string? str)
        {
            if (str == null) return "";

            // Remove HTML tags first
            str = Regex.Replace(str, "<[^>]*>", ""); // More robust HTML tag removal

            // Truncate long descriptions and clean up for frontmatter
            var maxLength = 150;
            str = str.Length > maxLength ? str[..maxLength] + "..." : str;

            return str
                .Replace("\n", " ")
                .Replace("\"", "'")
                .Replace("\\", "")
                .Replace(":", "-")
                .Replace("<", "")
                .Replace(">", "")
                .Trim();
        }

        string? FileEscape(string? str)
            => str?.Replace("<", "`").Replace(">", "`").Replace(" ", "%20");

        string SourceLink(Item item)
            => item.Source?.Remote == null
                ? ""
                : $"###### [View Source]({item.Source.Remote.Repo}/blob/{item.Source.Remote.Branch}/{item.Source.Remote.Path}#L{item.Source.StartLine + 1})";

        void Declaration(StringBuilder str, Item item)
        {
            str.AppendLine(SourceLink(item));
            if (item.Syntax != null)
            {
                str.AppendLine("```csharp title=\"Declaration\"");
                // Ensure curly brace snippets are properly formatted
                var content = item.Syntax.Content;
                if (content.TrimStart().StartsWith("{"))
                    content = $"    {content}"; // Add indentation for standalone blocks
                str.AppendLine(content);
                str.AppendLine("```");
            }
        }

        static string FormatTypeName(string name)
        {
            // Wrap names containing type parameters, curly braces, or dictionary-like content in backticks
            if (name.Contains('<') ||
                name.Contains('{') ||
                name.Contains('}') ||
                (name.Contains(':') && name.Contains('[')) ||  // Likely a dictionary
                Regex.IsMatch(name, @"\{.*:.*\}")) // Match dictionary-like patterns
                return $"`{name}`";
            return name;
        }

        string? GetSummary(string? summary, bool linkFromGroupedType)
        {
            if (summary == null)
                return null;

            // Remove nested p tags and replace with line breaks
            summary = Regex.Replace(summary, @"<p>\s*", "\n\n");
            summary = Regex.Replace(summary, @"\s*</p>", "");

            // Handle example blocks
            summary = Regex.Replace(summary, @"<example>(.*?)</example>", m =>
            {
                var content = m.Groups[1].Value.Trim();
                return $"\n\n**Example:**\n```csharp\n{content}\n```\n";
            });

            // Replace other HTML tags with markdown - ensure tags are properly closed
            summary = xrefRegex.Replace(summary, match =>
            {
                var uid = match.Groups[1].Value;
                return Link(uid, linkFromGroupedType);
            });
            summary = langwordXrefRegex.Replace(summary, match => $"`{match.Groups[1].Value}`");
            summary = codeBlockRegex.Replace(summary, match => $"```csharp\n{match.Groups[1].Value.Trim()}\n```");

            // Ensure inline code tags are properly closed
            summary = Regex.Replace(summary, @"<code>([^<]*)</code>", match => $"`{match.Groups[1].Value}`");
            summary = Regex.Replace(summary, @"<code>([^<]*)", match => $"`{match.Groups[1].Value}`"); // Handle unclosed tags

            summary = linkRegex.Replace(summary, match => $"[{match.Groups[2].Value}]({match.Groups[1].Value})");
            summary = brRegex.Replace(summary, _ => "\n\n");

            // Clean up any remaining HTML tags
            summary = Regex.Replace(summary, @"<[^>]*>", "");

            // Handle dictionary-like content
            summary = Regex.Replace(summary, @"\{[^}]*:[^}]*\}", match => $"`{match.Value}`");

            // Clean up multiple newlines
            summary = Regex.Replace(summary, @"\n{3,}", "\n\n");

            if (config.ForceNewline)
                summary = summary.Replace("\n", config.ForcedNewline);

            return HtmlEscape(summary)?.Trim();
        }

        Log73.Console.Info("Generating and writing markdown...");

        // if grouping types, count types in each namespace, for minCount
        // we have to make it a local method like this because of the ref there(cannot be in async method)
        static void DoTypeCounts(List<Item> items, Dictionary<string, int> typeCounts)
        {
            foreach (var item in items)
            {
                if (item.Type is not ("Class" or "Interface" or "Enum" or "Struct" or "Delegate")) continue;
                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(typeCounts, item.Namespace, out var exists);
                if (exists)
                    count++;
                else
                    count = 1;
            }
        }

        Dictionary<string, int>? typeCounts = null;
        if (config.TypesGrouping?.Enabled ?? false)
        {
            typeCounts = new();
            DoTypeCounts(items, typeCounts);
        }

        bool NamespaceHasTypeGrouping(string @namespace)
            => typeCounts is not null && typeCounts.TryGetValue(@namespace, out var count) &&
               count >= config.TypesGrouping!.MinCount;

        string Link(string uid, bool linkFromGroupedType, bool nameOnly = false, bool linkFromIndex = false)
        {
            var reference = items.FirstOrDefault(i => i.Uid == uid);
            if (uid.Contains('{') && reference == null)
            {
                // try to resolve single type argument references
                var replaced = uid.Replace(uid[uid.IndexOf('{')..(uid.LastIndexOf('}') + 1)], "`1");
                reference = items.FirstOrDefault(i => i.Uid == replaced);
            }

            if (reference == null)
                return $"`{uid}`"; // Ensure unknown references are code-wrapped

            var name = nameOnly ? reference.Name : reference.FullName;
            // Wrap type parameters in backticks
            if (name.Contains('<'))
                name = $"`{name}`";

            var dots = linkFromIndex ? "./" : linkFromGroupedType ? "../../" : "../";
            var extension = linkFromIndex ? ".md" : "";
            if (reference.Type is "Class" or "Interface" or "Enum" or "Struct" or "Delegate")
            {
                if (NamespaceHasTypeGrouping(reference.Namespace))
                    return
                        $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}/{GetTypePathPart(reference.Type)}/{reference.Name}{extension}")})";
                return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}/{reference.Name}{extension}")})";
            }
            else if (reference.Type is "Namespace")
            {
                if (config.RewriteInterlinks)
                {
                    if (linkFromIndex)
                    {
                        return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Name}/{reference.Name}{extension}")})";
                    }
                    else
                    {
                        return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Name}")})";
                    }
                }
                return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Name}/{reference.Name}{extension}")})";
            }
            else
            {
                var parent = items.FirstOrDefault(i => i.Uid == reference.Parent);
                if (parent == null)
                    return $"`{uid}`"; // Ensure unknown references are code-wrapped
                return
                    $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}{(NamespaceHasTypeGrouping(parent.Namespace) ? $"/{GetTypePathPart(parent.Type)}" : "")}/{parent.Name}{extension}")}#{reference.Name.ToLower().Replace("(", "").Replace(")", "").Replace("?", "")})";
            }
        }

        stopwatch.Restart();
        // create type files finally
        await Parallel.ForEachAsync(items, async (item, _) =>
        {
            // for global namespace?
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (item.CommentId == null)
            {
                if (item.Type == "Namespace")
                    return;
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                Log73.Console.Warn($"Missing commentId for {item.Uid ?? item.Id ?? "(can't get uid or id)"}");
                return;
            }

            if (item.CommentId.StartsWith("T:"))
            {
                var isGroupedType = typeCounts != null && typeCounts[item.Namespace] >= config.TypesGrouping!.MinCount;
                var str = new StringBuilder();
                str.AppendLine("---");
                str.AppendLine($"title: {item.Type} {item.Name}");
                str.AppendLine($"sidebar_label: {item.Name}");
                if (item.Summary != null)
                    str.AppendLine($"description: \"{SafeFrontmatterValue(item.Summary)}\"");
                str.AppendLine("---");
                str.AppendLine($"# {item.Type} {FormatTypeName(item.Name)}");
                str.AppendLine(GetSummary(item.Summary, isGroupedType)?.Trim());
                str.AppendLine();
                str.AppendLine($"###### **Assembly**: {item.Assemblies[0]}.dll");
                Declaration(str, item);
                // do not when it is only System.Object
                if (item.Inheritance?.Length > 1)
                {
                    str.Append("**Inheritance:** ");
                    for (int i = 0; i < item.Inheritance.Length; i++)
                    {
                        str.Append(Link(item.Inheritance[i], isGroupedType));
                        if (i != item.Inheritance.Length - 1)
                            str.Append(" -> ");
                    }

                    str.Append("\n\n");
                }

                if (item.DerivedClasses != null)
                {
                    str.AppendLine("**Derived:**  ");
                    if (item.DerivedClasses.Length > 8)
                        str.AppendLine("\n<details>\n<summary>Expand</summary>\n");

                    for (var i = 0; i < item.DerivedClasses.Length; i++)
                    {
                        str.Append(Link(item.DerivedClasses[i], isGroupedType));
                        if (i != item.DerivedClasses.Length - 1)
                            str.Append(", ");
                    }

                    if (item.DerivedClasses.Length > 8)
                        str.AppendLine("\n</details>\n");
                    str.Append("\n\n");
                }

                if (item.Implements != null)
                {
                    str.AppendLine("**Implements:**  ");
                    if (item.Implements.Length > 8)
                        str.AppendLine("\n<details>\n<summary>Expand</summary>\n");

                    for (var i = 0; i < item.Implements.Length; i++)
                    {
                        str.Append(Link(item.Implements[i], isGroupedType));
                        if (i != item.Implements.Length - 1)
                            str.Append(", ");
                    }

                    if (item.Implements.Length > 8)
                        str.AppendLine("\n</details>\n");
                    str.Append("\n\n");
                }

                // Properties
                var properties = GetProperties(item.Uid);
                if (properties.Length != 0)
                {
                    str.AppendLine("## Properties");
                    foreach (var property in properties)
                    {
                        str.AppendLine($"### {FormatTypeName(property.Name)}");
                        str.AppendLine(GetSummary(property.Summary, isGroupedType)?.Trim());
                        Declaration(str, property);
                    }
                }

                // Fields
                var fields = GetFields(item.Uid);
                if (fields.Length != 0)
                {
                    str.AppendLine("## Fields");
                    foreach (var field in fields)
                    {
                        str.AppendLine($"### {FormatTypeName(field.Name)}");
                        str.AppendLine(GetSummary(field.Summary, isGroupedType)?.Trim());
                        Declaration(str, field);
                    }
                }

                // Methods
                var methods = GetMethods(item.Uid);
                if (methods.Length != 0)
                {
                    str.AppendLine("## Methods");
                    foreach (var method in methods)
                    {
                        str.AppendLine($"### {FormatTypeName(method.Name)}");
                        str.AppendLine(GetSummary(method.Summary, isGroupedType)?.Trim());
                        Declaration(str, method);
                        if (!string.IsNullOrWhiteSpace(method.Syntax!.Return?.Type))
                        {
                            str.AppendLine();
                            str.AppendLine("##### Returns");
                            str.AppendLine();
                            str.Append(Link(method.Syntax.Return.Type, isGroupedType).Trim());
                            if (string.IsNullOrWhiteSpace(method.Syntax.Return?.Description))
                                str.AppendLine();
                            else
                                str.Append(": " + GetSummary(method.Syntax.Return.Description, isGroupedType));
                        }

                        if (method.Syntax.Parameters is { Length: > 0 })
                        {
                            str.AppendLine();
                            str.AppendLine("##### Parameters");
                            str.AppendLine();
                            if (method.Syntax.Parameters.Any(p => !string.IsNullOrWhiteSpace(p.Description)))
                            {
                                str.AppendLine("| Type | Name | Description |");
                                str.AppendLine("|:--- |:--- |:--- |");
                                foreach (var parameter in method.Syntax.Parameters)
                                    str.AppendLine(
                                        $"| {Link(parameter.Type, isGroupedType)} | *{parameter.Id}* | {GetSummary(parameter.Description, isGroupedType)} |");
                            }
                            else
                            {
                                str.AppendLine("| Type | Name |");
                                str.AppendLine("|:--- |:--- |");
                                foreach (var parameter in method.Syntax.Parameters)
                                    str.AppendLine(
                                        $"| {Link(parameter.Type, isGroupedType)} | *{parameter.Id}* |");
                            }

                            str.AppendLine();
                        }

                        if (method.Syntax.TypeParameters is { Length: > 0 })
                        {
                            str.AppendLine("##### Type Parameters");
                            if (method.Syntax.TypeParameters.Any(tp => !string.IsNullOrWhiteSpace(tp.Description)))
                            {
                                str.AppendLine("| Name | Description |");
                                str.AppendLine("|:--- |:--- |");
                                foreach (var typeParameter in method.Syntax.TypeParameters)
                                    str.AppendLine(
                                        $"| {Link(typeParameter.Id, isGroupedType)} | {typeParameter.Description} |");
                            }
                            else
                                foreach (var typeParameter in method.Syntax.TypeParameters)
                                    str.AppendLine($"* {Link(typeParameter.Id, isGroupedType)}");
                        }

                        if (method.Exceptions is { Length: > 0 })
                        {
                            str.AppendLine();
                            str.AppendLine("##### Exceptions");
                            str.AppendLine();
                            foreach (var exception in method.Exceptions)
                            {
                                // those two spaces are there so that we can have a line break without too much spacing
                                // before the next line
                                str.AppendLine($"{Link(exception.Type, isGroupedType)}  ");
                                str.AppendLine(GetSummary(exception.Description, isGroupedType)?.Trim());
                            }
                        }
                    }
                }

                // Events
                var events = GetEvents(item.Uid);
                if (events.Length != 0)
                {
                    str.AppendLine("## Events");
                    foreach (var @event in events)
                    {
                        str.AppendLine($"### {FormatTypeName(@event.Name)}");
                        str.AppendLine(GetSummary(@event.Summary, isGroupedType)?.Trim());
                        Declaration(str, @event);
                        str.AppendLine("##### Event Type");
                        if (@event.Syntax!.Return!.Description == null)
                            str.AppendLine(Link(@event.Syntax.Return.Type, isGroupedType).Trim());
                        else
                            str.AppendLine(Link(@event.Syntax.Return.Type, isGroupedType).Trim() + ": " +
                                           @event.Syntax.Return.Description);
                    }
                }

                // Implements
                if (item.Implements?.Any() ?? false)
                {
                    str.AppendLine();
                    str.AppendLine("## Implements");
                    str.AppendLine();
                    foreach (var implemented in item.Implements)
                    {
                        str.AppendLine($"* {Link(implemented, isGroupedType)}");
                    }
                }

                // Extension methods
                if (item.ExtensionMethods is { Length: > 1 })
                {
                    str.AppendLine("## Extension Methods");
                    foreach (var extMethod in item.ExtensionMethods!)
                    {
                        // ReSharper disable once SimplifyConditionalTernaryExpression
                        // todo: wont link if other args are present
                        var method = items.FirstOrDefault(i =>
                            ((i.Syntax?.Parameters?.Any() ?? false)
                                ? (i.Syntax.Parameters[0].Type + '.' +
                                   i.FullName
                                       [..(i.FullName.IndexOf('(') == -1 ? i.FullName.Length : i.FullName.IndexOf('('))] ==
                                   extMethod)
                                : false));
                        if (method == null)
                            str.AppendLine($"* {extMethod.Replace("{", "&#123;").Replace("}", "&#125;")}");
                        else
                            str.AppendLine($"* {Link(method.Uid, isGroupedType)}");
                    }
                }

                var path = !isGroupedType
                    ? Path.Join(config.OutputPath, item.Namespace, item.Name.Replace('<', '`').Replace('>', '`')) + ".md"
                    : Path.Join(config.OutputPath, item.Namespace, GetTypePathPart(item.Type),
                        item.Name.Replace('<', '`').Replace('>', '`')) + ".md";

                // create directory if it doesn't exist
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                await File.WriteAllTextAsync(path, str.ToString());
            }
            else if (item.Type == "Namespace")
            {
                var str = new StringBuilder();
                str.AppendLine("---");
                str.AppendLine($"title: {item.Type} {item.Name}");
                str.AppendLine($"sidebar_label: {item.Name}");
                str.AppendLine("---");
                str.AppendLine($"# Namespace {HtmlEscape(item.Name)}");

                void Do(string type, string header)
                {
                    var where = items.Where(i => i.Namespace == item.Name && i.Type == type).ToArray();
                    if (where.Length != 0)
                    {
                        str.AppendLine($"## {header}");
                        foreach (var item1 in where.OrderBy(i => i.Name))
                        {
                            str.AppendLine($"### {HtmlEscape(Link(item1.Uid, false, nameOnly: true))}");
                            str.AppendLine(GetSummary(item1.Summary, false)?.Trim());
                        }
                    }
                }

                Do("Class", "Classes");
                Do("Struct", "Structs");
                Do("Interface", "Interfaces");
                Do("Enum", "Enums");
                Do("Delegate", "Delegates");

                await File.WriteAllTextAsync(Path.Join(config.OutputPath, item.Name, $"{item.Name}.md"), str.ToString());
            }
        });
        Log73.Console.Info($"Generated markdown in {stopwatch.ElapsedMilliseconds}ms.");
    }
}