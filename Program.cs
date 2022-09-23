using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DocFxMarkdownGen;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using ILoggerFactory loggerFactory =
    LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "hh:mm:ss ";
        }));

var logger = loggerFactory.CreateLogger<Program>();

var versionString = Assembly.GetEntryAssembly()?
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();


var xrefRegex = new Regex("<xref href=\"(.+?)\" data-throw-if-not-resolved=\"false\"></xref>", RegexOptions.Compiled);
var langwordXrefRegex =
    new Regex("<xref uid=\"langword_csharp_.+?\" name=\"(.+?)\" href=\"\"></xref>", RegexOptions.Compiled);
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
Dictionary<string,Item> items = new();

#region read all yaml and create directory structure

await Parallel.ForEachAsync(Directory.GetFiles(config.YamlPath, "*.yml"), async (file, _) =>
{
    if (file.EndsWith("toc.yml"))
        return;
    logger.LogDebug(file);
    var obj = yamlDeserializer.Deserialize<DocFxFile>(await File.ReadAllTextAsync(file));
    lock (items)
    {
        foreach (var item in obj.Items)
        {
            items.Add(item.Uid, item);
        }
    }
});
logger.LogInformation($"Read all YAML in {stopwatch.ElapsedMilliseconds}ms.");
// create namespace directories
await Parallel.ForEachAsync(items, async (kvp, _) =>
{
    var item = kvp.Value;
    if (item.Type == "Namespace")
    {
        logger.LogDebug(item.Type + ": " + item.Name);
        var dir = Path.Combine(config.OutputPath, item.Name);
        Directory.CreateDirectory(dir);
    }
});

#endregion

// util methods
Item[] GetProperties(string uid)
    => items.Values.Where(i => i.Parent == uid && i.Type == "Property").ToArray();

Item[] GetFields(string uid)
    => items.Values.Where(i => i.Parent == uid && i.Type == "Field").ToArray();

Item[] GetMethods(string uid)
    => items.Values.Where(i => i.Parent == uid && i.Type == "Method").ToArray();

Item[] GetEvents(string uid)
    => items.Values.Where(i => i.Parent == uid && i.Type == "Event").ToArray();

Item[] GetInheritedMethods(string uid)
{
    if(uid == "System.Object")
        return Array.Empty<Item>();
    var item = TryGet(uid);
    if (item == null || item?.InheritedMembers == null || item?.Inheritance == null || item.Inheritance.Length == 0)
        return Array.Empty<Item>();
    if(item.Inheritance.Last() == "System.Object")
        return Array.Empty<Item>();
    var baseClass = TryGet(item.Inheritance.Last());
    if (baseClass == null)
        return Array.Empty<Item>();
    var results = TryGetAll(baseClass.Children).Where(x => x.Type == "Method").ToArray();
    return results;
}

Item[] GetInheritedProperties(string uid)
{
    if(uid == "System.Object")
        return Array.Empty<Item>();
    var item = TryGet(uid);
    if (item == null || item?.InheritedMembers == null || item?.Inheritance == null || item.Inheritance.Length == 0)
        return Array.Empty<Item>();
    if(item.Inheritance.Last() == "System.Object")
        return Array.Empty<Item>();
    var baseClass = TryGet(item.Inheritance.Last());
    if (baseClass == null)
        return Array.Empty<Item>();
    var results = TryGetAll(baseClass.Children).Where(x => x.Type == "Property").ToArray();
    return results;
}

Item? TryGet(string uid)
{
    return items.ContainsKey(uid) ? items[uid] : null;
}

Item[] TryGetAll(string[] uids)
{
    var result = new List<Item>();
    foreach (var uid in uids)
    {
        var item = TryGet(uid);
        if (item != null)
            result.Add(item);
    }

    return result.ToArray();
}

string Link(string uid, bool nameOnly = false, bool indexLink = false)
{
    var reference = TryGet(uid);
    if (uid.Contains('{') && reference == null)
    {
        // try to resolve single type argument references
        var replaced = uid.Replace(uid[uid.IndexOf('{')..(uid.LastIndexOf('}') + 1)], "`1");
        reference = TryGet(replaced);
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
        var parent = TryGet(reference.Parent);
        if (parent == null)
            return $"`{uid.Replace('{', '<').Replace('}', '>')}`";
        return
            $"[{HtmlEscape(name)}]({FileEscape($"{dots}{reference.Namespace}/{parent.Name}{extension}")}#{reference.Name.ToLower().Replace("(", "").Replace(")", "")})";
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
    summary = codeRegex.Replace(summary, match => $"`{match.Groups[1].Value}`");
    summary = linkRegex.Replace(summary, match => $"[{match.Groups[2].Value}]({match.Groups[1].Value})");

    return HtmlEscape(summary);
}

string? HtmlEscape(string? str)
    => str?.Replace("<", "&lt;")?.Replace(">", "&gt;");

string? FileEscape(string? str)
    => str?.Replace("<", "`")?.Replace(">", "`");


string SourceLink(Item item)
    =>
        item.Source?.Remote?.Repo != null ? $"###### [View Source]({item.Source.Remote.Repo}/blob/{item.Source.Remote.Branch}/{item.Source.Remote.Path}#L{item.Source.StartLine + 1})" : "";

void Declaration(StringBuilder str, Item item)
{
    str.AppendLine(SourceLink(item));
    str.AppendLine("```csharp title=\"Declaration\"");
    str.AppendLine(item.Syntax.Content);
    str.AppendLine("```");
}

void MethodSummary(StringBuilder str, Item method)
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
}

logger.LogInformation("Generating and writing markdown...");
stopwatch.Restart();
// create type files finally
await Parallel.ForEachAsync(items, async (kvp, _) =>
{
    var item = kvp.Value;
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
        
        // Inherited Properties
        try
        {
            var inheritedProperties = GetInheritedProperties(item.Uid);
            if (inheritedProperties.Length > 0)
            {
                str.AppendLine("## Inherited Properties");
                foreach (var property in inheritedProperties)
                {
                    str.AppendLine($"### {property.Name}");
                    str.AppendLine(GetSummary(property.Summary)?.Trim());
                    Declaration(str, property);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
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
                /// write method details
                MethodSummary(str, method);
            }
        }

        var inheritedMethods = GetInheritedMethods(item.Uid);
        if (inheritedMethods.Length > 0)
        {
            str.AppendLine("## Inherited Methods");
            foreach (var inheritedMethod in inheritedMethods)
            {
                MethodSummary(str, inheritedMethod);
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

        await File.WriteAllTextAsync(
            Path.Join(config.OutputPath, item.Namespace, item.Name.Replace('<', '`').Replace('>', '`')) + ".md",
            str.ToString());
    }
    else if (item.Type == "Namespace")
    {
        var str = new StringBuilder();
        str.AppendLine("---");
        str.AppendLine("title: " + item.Type + " " + item.Name);
        str.AppendLine("sidebar_label: Index");
        str.AppendLine("sidebar_position: 0");
        str.AppendLine("---");
        str.AppendLine($"# Namespace {HtmlEscape(item.Name)}");

        void Do(string type, string header)
        {
            var @where = items.Values.Where(i => i.Namespace == item.Name && i.Type == type);
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
    foreach (var @namespace in items.Values.Where(i => i.Type == "Namespace").OrderBy(i => i.Name))
        str.AppendLine($"* {HtmlEscape(Link(@namespace.Uid, indexLink: true))}");
    str.AppendLine();
    str.AppendLine("---");
    str.AppendLine(
        $"Generated using [DocFxMarkdownGen](https://github.com/Jan0660/DocFxMarkdownGen) v{versionString}.");
    await File.WriteAllTextAsync(Path.Join(config.OutputPath, $"index.md"), str.ToString());
}
logger.LogInformation($"Markdown finished in {stopwatch.ElapsedMilliseconds}ms.");

// classes
