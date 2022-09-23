using YamlDotNet.Serialization;

namespace DocFxMarkdownGen;

public class DocFxFile
{
    public Item[] Items { get; set; }
}

public class Item
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
    
    public string[]? InheritedMembers { get; set; }

    public string[] ExtensionMethods { get; set; }
    // modifiers.csharp
    // modifiers.vb
}

public class Syntax
{
    public string Content { get; set; }
    [YamlMember(Alias = "content.vb")] public string ContentVb { get; set; }
    public Parameter[] Parameters { get; set; }
    public TypeParameter[] TypeParameters { get; set; }
    public SyntaxReturn Return { get; set; }
}

public class Source
{
    public Remote Remote { get; set; }
    public string Id { get; set; }
    public string Path { get; set; }
    public int StartLine { get; set; }
}

public class Remote
{
    public string Path { get; set; }
    public string Branch { get; set; }
    public string Repo { get; set; }
}

public class Parameter
{
    public string Id { get; set; }
    public string Type { get; set; }
    public string? Description { get; set; }
}

public class TypeParameter
{
    public string Id { get; set; }
    public string Description { get; set; }
}

public class SyntaxReturn
{
    public string Type { get; set; }
    public string? Description { get; set; }
}

public class Config
{
    public string YamlPath { get; set; }
    public string OutputPath { get; set; }
    public string IndexSlug { get; set; }
}