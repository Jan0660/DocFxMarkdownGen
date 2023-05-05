using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Log73;
using static Log73.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618

Options.UseAnsi = false;
if (Environment.GetEnvironmentVariable("JAN_DEBUG") == "1")
    Options.LogLevel = LogLevel.Debug;
var versionString = Assembly.GetEntryAssembly()?
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
WriteLine($"DocFxMarkdownGen v{versionString} running...");

var xrefRegex = new Regex("<xref href=\"(.+?)\" data-throw-if-not-resolved=\"false\"></xref>", RegexOptions.Compiled);
var langwordXrefRegex =
    new Regex("<xref uid=\"langword_csharp_.+?\" name=\"(.+?)\" href=\"\"></xref>", RegexOptions.Compiled);
var codeBlockRegex = new Regex("<pre><code class=\"lang-csharp\">((.|\n)+?)</code></pre>", RegexOptions.Compiled);
var codeRegex = new Regex("<code>(.+?)</code>", RegexOptions.Compiled);
var linkRegex = new Regex("<a href=\"(.+?)\">(.+?)</a>", RegexOptions.Compiled);
var yamlDeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties().Build();
var config = yamlDeserializer.Deserialize<Config>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("DFMG_CONFIG") ?? "./config.yaml"));
config.IndexSlug ??= "/api";
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
    Debug(file);
    var obj = yamlDeserializer.Deserialize<DocFxFile>(await File.ReadAllTextAsync(file));
    lock (items)
    {
        items.AddRange(obj.Items);
    }
});
Info($"Read all YAML in {stopwatch.ElapsedMilliseconds}ms.");
// create namespace directories
await Parallel.ForEachAsync(items, async (item, _) =>
{
    if (item.Type == "Namespace")
    {
        Debug(item.Type + ": " + item.Name);
        var dir = Path.Combine(config.OutputPath, item.Name);
        Directory.CreateDirectory(dir);
    }
});

#endregion

// util methods
Item[] GetProperties(string uid)
    => items.Where(i => i.Parent == uid && i.Type == "Property").ToArray();

Item[] GetFields(string uid)
    => items.Where(i => i.Parent == uid && i.Type == "Field").ToArray();

Item[] GetMethods(string uid)
    => items.Where(i => i.Parent == uid && i.Type == "Method").ToArray();

Item[] GetEvents(string uid)
    => items.Where(i => i.Parent == uid && i.Type == "Event").ToArray();

string Link(string uid, bool nameOnly = false, bool indexLink = false)
{
    var reference = items.FirstOrDefault(i => i.Uid == uid);
    if (uid.Contains('{') && reference == null)
    {
        // try to resolve single type argument references
        var replaced = uid.Replace(uid[uid.IndexOf('{')..(uid.LastIndexOf('}') + 1)], "`1");
        reference = items.FirstOrDefault(i => i.Uid == replaced);
    }
    if (reference == null)
        // todo: try to resolve to msdn links if System namespace maybe
        return $"`{uid.Replace('{', '<').Replace('}', '>')}`";
    var name = nameOnly ? reference.Name : reference.FullName;
    var dots = indexLink ? "./" : "../";
    var extension = indexLink ? ".md" : "";
    if (reference.Type is "Class" or "Interface" or "Enum" or "Struct" or "Delegate")
        return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}/{reference.Name}{extension}")})";
    else if (reference.Type is "Namespace")
        return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Name}/{reference.Name}{extension}")})";
    else
    {
        var parent = items.FirstOrDefault(i => i.Uid == reference.Parent);
        if (parent == null)
            return $"`{uid.Replace('{', '<').Replace('}', '>')}`";
        return
            $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}/{parent.Name}{extension}")}#{reference.Name.ToLower().Replace("(", "").Replace(")", "").Replace("?", "")})";
    }
}

string? GetSummary(string? summary)
{
    if (summary == null)
        return null;
    summary = xrefRegex.Replace(summary, match =>
    {
        var uid = match.Groups[1].Value;
        return Link(uid);
    });
    summary = langwordXrefRegex.Replace(summary, match => $"`{match.Groups[1].Value}`");
    summary = codeBlockRegex.Replace(summary, match => $"```csharp\n{match.Groups[1].Value.Trim()}\n```");
    summary = codeRegex.Replace(summary, match => $"`{match.Groups[1].Value}`");
    summary = linkRegex.Replace(summary, match => $"[{match.Groups[2].Value}]({match.Groups[1].Value})");

    return HtmlEscape(summary);
}

string? HtmlEscape(string? str)
    => str?.Replace("<", "&lt;")?.Replace(">", "&gt;");

string? FileEscape(string? str)
    => str?.Replace("<", "`")?.Replace(">", "`");


string SourceLink(Item item)
    => item.Source?.Remote == null ? "" :
        $"###### [View Source]({item.Source.Remote.Repo}/blob/{item.Source.Remote.Branch}/{item.Source.Remote.Path}#L{item.Source.StartLine + 1})";

void Declaration(StringBuilder str, Item item)
{
    str.AppendLine(SourceLink(item));
    str.AppendLine("```csharp title=\"Declaration\"");
    str.AppendLine(item.Syntax.Content);
    str.AppendLine("```");
}

Info("Generating and writing markdown...");
stopwatch.Restart();
// create type files finally
await Parallel.ForEachAsync(items, async (item, _) =>
{
    if (item.CommentId.StartsWith("T:"))
    {
        var str = new StringBuilder();
        str.AppendLine("---");
        str.AppendLine("title: " + item.Type + " " + item.Name);
        str.AppendLine("sidebar_label: " + item.Name);
        if (item.Summary != null)
            // todo: run a regex replace to get rid of hyperlinks and inline code blocks?
            str.AppendLine($"description: \"{GetSummary(item.Summary)?.Trim().Replace("\"", "\\\"")}\"");
        str.AppendLine("---");
        str.AppendLine($"# {item.Type} {HtmlEscape(item.Name)}");
        str.AppendLine(GetSummary(item.Summary)?.Trim());
        str.AppendLine();
        str.AppendLine($"###### **Assembly**: {item.Assemblies[0]}.dll");
        Declaration(str, item);
        // Properties
        var properties = GetProperties(item.Uid);
        if (properties.Length != 0)
        {
            str.AppendLine("## Properties");
            foreach (var property in properties)
            {
                str.AppendLine($"### {property.Name}");
                str.AppendLine(GetSummary(property.Summary)?.Trim());
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
                str.AppendLine($"### {field.Name}");
                str.AppendLine(GetSummary(field.Summary)?.Trim());
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
                str.AppendLine($"### {HtmlEscape(method.Name)}");
                str.AppendLine(GetSummary(method.Summary)?.Trim());
                Declaration(str, method);
                if (!string.IsNullOrWhiteSpace(method.Syntax.Return?.Type))
                {
                    str.AppendLine();
                    str.AppendLine("##### Returns");
                    str.AppendLine();
                    str.Append(Link(method.Syntax.Return.Type)?.Trim());
                    if (string.IsNullOrWhiteSpace(method.Syntax.Return?.Description))
                        str.AppendLine();
                    else
                        str.Append(": " + GetSummary(method.Syntax.Return.Description));
                }

                if ((method.Syntax.Parameters?.Length ?? 0) != 0)
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
                                $"| {Link(parameter.Type)} | *{parameter.Id}* | {GetSummary(parameter.Description)} |");
                    }
                    else
                    {
                        str.AppendLine("| Type | Name |");
                        str.AppendLine("|:--- |:--- |");
                        foreach (var parameter in method.Syntax.Parameters)
                            str.AppendLine(
                                $"| {Link(parameter.Type)} | *{parameter.Id}* |");
                    }

                    str.AppendLine();
                }

                if ((method.Syntax.TypeParameters?.Length ?? 0) != 0)
                {
                    str.AppendLine("##### Type Parameters");
                    if (method.Syntax.TypeParameters.Any(tp => !string.IsNullOrWhiteSpace(tp.Description)))
                    {
                        str.AppendLine("| Name | Description |");
                        str.AppendLine("|:--- |:--- |");
                        foreach (var typeParameter in method.Syntax.TypeParameters)
                            str.AppendLine($"| {Link(typeParameter.Id)} | {typeParameter.Description} |");
                    }
                    else
                        foreach (var typeParameter in method.Syntax.TypeParameters)
                            str.AppendLine($"* {Link(typeParameter.Id)}");
                }

                if((method.Exceptions?.Length ?? 0) != 0)
                {
                    str.AppendLine();
                    str.AppendLine("##### Exceptions");
                    str.AppendLine();
                    foreach (var exception in method.Exceptions)
                    {
                        // those two spaces are there so that we can have a line break without too much spacing
                        // before the next line
                        str.AppendLine($"[{exception.Type}]({Link(exception.Type)})  ");
                        str.AppendLine(GetSummary(exception.Description)?.Trim());
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
                str.AppendLine($"### {HtmlEscape(@event.Name)}");
                str.AppendLine(GetSummary(@event.Summary)?.Trim());
                Declaration(str, @event);
                str.AppendLine("##### Event Type");
                if (@event.Syntax.Return.Description == null)
                    str.AppendLine(Link(@event.Syntax.Return.Type)?.Trim());
                else
                    str.AppendLine(Link(@event.Syntax.Return.Type)?.Trim() + ": " + @event.Syntax.Return.Description);
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
                str.AppendLine($"* {Link(implemented)}");
            }
        }


        // Extension methods
        if ((item.ExtensionMethods?.Length ?? 0) != 0)
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
                    str.AppendLine($"* {extMethod}");
                else
                    str.AppendLine($"* {Link(method.Uid)}");
            }
        }

        await File.WriteAllTextAsync(
            Path.Join(config.OutputPath, item.Namespace, item.Name.Replace('<', '`').Replace('>', '`')) + ".md",
            str.ToString());
    }
    else if (item.Type == "Namespace")
    {
        var str = new StringBuilder();
        str.AppendLine("---");
        str.AppendLine("title: " + item.Type + " " + item.Name);
        str.AppendLine("sidebar_label: " + item.Name);
        str.AppendLine("---");
        str.AppendLine($"# Namespace {HtmlEscape(item.Name)}");

        void Do(string type, string header)
        {
            var @where = items.Where(i => i.Namespace == item.Name && i.Type == type);
            if (@where.Any())
            {
                str.AppendLine($"## {header}");
                foreach (var item1 in @where.OrderBy(i => i.Name))
                {
                    str.AppendLine($"### {HtmlEscape(Link(item1.Uid, true))}");
                    str.AppendLine(GetSummary(item1.Summary)?.Trim());
                }
            }
        }

        Do("Class", "Classes");
        Do("Struct", "Structs");
        Do("Interface", "Interfaces");
        Do("Enum", "Enums");

        await File.WriteAllTextAsync(Path.Join(config.OutputPath, item.Name, $"{item.Name}.md"), str.ToString());
    }
});
// generate index page
{
    var str = new StringBuilder();
    str.AppendLine("---");
    str.AppendLine("title: Index");
    str.AppendLine("sidebar_label: Index");
    str.AppendLine("sidebar_position: 0");
    str.AppendLine($"slug: {config.IndexSlug}");
    str.AppendLine("---");
    str.AppendLine("# API Index");
    str.AppendLine("## Namespaces");
    foreach (var @namespace in items.Where(i => i.Type == "Namespace").OrderBy(i => i.Name))
        str.AppendLine($"* {HtmlEscape(Link(@namespace.Uid, indexLink: true))}");
    str.AppendLine();
    str.AppendLine("---");
    str.AppendLine(
        $"Generated using [DocFxMarkdownGen](https://github.com/Jan0660/DocFxMarkdownGen) v{versionString}.");
    await File.WriteAllTextAsync(Path.Join(config.OutputPath, $"index.md"), str.ToString());
}
Info($"Markdown finished in {stopwatch.ElapsedMilliseconds}ms.");

// classes
class DocFxFile
{
    public Item[] Items { get; set; }
}

class Item
{
    public string Uid { get; set; }
    public string CommentId { get; set; }
    public string Id { get; set; }
    public string Parent { get; set; }
    public string[] Children { get; set; }
    public string[] Langs { get; set; }
    public string Definition { get; set; }
    public string Name { get; set; }
    public string NameWithType { get; set; }
    public string FullName { get; set; }

    public string Type { get; set; }

    public Source Source { get; set; }
    public string[] Assemblies { get; set; }

    public string Namespace { get; set; }

    // todo: trim when loading instead of when usig gnfgnrjfuijik
    public string? Summary { get; set; }

    // todo: example
    public Syntax Syntax { get; set; }

    public string[] Inheritance { get; set; }
    public string[]? Implements { get; set; }

    public string[] ExtensionMethods { get; set; }
    // modifiers.csharp
    // modifiers.vb
    public ThrowsException[]? Exceptions { get; set; }
}

class ThrowsException
{
    public string Type { get; set; }
    public string CommentId { get; set; }
    public string Description { get; set; }
}

class Syntax
{
    public string Content { get; set; }
    [YamlMember(Alias = "content.vb")] public string ContentVb { get; set; }
    public Parameter[] Parameters { get; set; }
    public TypeParameter[] TypeParameters { get; set; }
    public SyntaxReturn Return { get; set; }
}

class Source
{
    public Remote Remote { get; set; }
    public string Id { get; set; }
    public string Path { get; set; }
    public int StartLine { get; set; }
}

class Remote
{
    public string Path { get; set; }
    public string Branch { get; set; }
    public string Repo { get; set; }
}

class Parameter
{
    public string Id { get; set; }
    public string Type { get; set; }
    public string? Description { get; set; }
}

class TypeParameter
{
    public string Id { get; set; }
    public string Description { get; set; }
}

class SyntaxReturn
{
    public string Type { get; set; }
    public string? Description { get; set; }
}

class Config
{
    public string YamlPath { get; set; }
    public string OutputPath { get; set; }
    public string IndexSlug { get; set; }
}