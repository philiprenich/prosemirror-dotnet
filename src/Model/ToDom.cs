using System.Text.Json;
using System.Text.Json.Nodes;
using HtmlAgilityPack;
using StepWise.Prose.Collections;
using StepWise.Prose.Model;

namespace StepWise.ProseMirror.Model;

public class SerializeOptions
{
    public HtmlDocument? Document { get; set; }
}

public class DomSerializer
{
    public readonly Dictionary<string, Func<Node, DomOutputSpec>> Nodes;
    public readonly Dictionary<string, Func<Mark, bool, DomOutputSpec>> Marks;
    
    public DomSerializer(Schema schema)
    {
        Nodes = NodesFromSchema(schema);
        Marks = MarksFromSchema(schema);
    }

    public HtmlNode SerializeFragment(Fragment fragment, SerializeOptions? options, HtmlNode? target)
    {
        target ??= Utils.Doc(options?.Document).DocumentNode;

        HtmlNode top = target;
        List<(Mark, HtmlNode)> active = [];

        fragment.ForEach((node, p, i) =>
        {
            if (active.Count > 0 || node.Marks.Count > 0)
            {
                int keep = 0;
                int rendered = 0;

                while (keep < active.Count && rendered < node.Marks.Count)
                {
                    Mark next = node.Marks[rendered];

                    if (!Marks.ContainsKey(next.Type.Name))
                    {
                        rendered++;
                        continue;
                    }

                    if (!next.Eq(active[keep].Item1) || next.Type.Spec.Spanning == false)
                    {
                        break;
                    }

                    keep++;
                    rendered++;
                }

                while (keep < active.Count)
                {
                    top = active.pop().Item2;
                }

                while (rendered < node.Marks.Count)
                {
                    Mark add = node.Marks[rendered++];
                    DomContent? markDom = SerializeMark(add, node.IsInline, options ?? new SerializeOptions());

                    if (markDom is not null)
                    {
                        active.Add((add, top));
                        top.AppendChild(markDom.Dom);
                        top = markDom.Content ?? markDom.Dom;
                    }
                }
            }

            top.AppendChild(SerializeNodeInner(node, options));
        });

        return top;
    }

    private HtmlNode SerializeNodeInner(Node node, SerializeOptions? options)
    {
        DomContent domContent = Utils.RenderSpec(Utils.Doc(options?.Document), Nodes.Single(x => x.Key == node.Type.Name).Value(node), node.Attrs);

        if (domContent.Content is not null)
        {
            if (node.IsLeaf)
            {
                throw new Exception("Content hole not allowed in a leaf node spec");
            }
            
            SerializeFragment(node.Content, options, domContent.Content);
        }

        return domContent.Dom;
    }

    public HtmlNode SerializeNode(Node node, SerializeOptions options)
    {
        HtmlNode dom = SerializeNodeInner(node, options);

        for (int i = node.Marks.Count - 1; i >= 0; i--)
        {
            DomContent? wrap = SerializeMark(node.Marks[i], node.IsInline, options);

            if (wrap is not null)
            {
                (wrap.Content ?? wrap.Dom).AppendChild(dom);

                dom = wrap.Dom;
            }
        }

        return dom;
    }

    private DomContent? SerializeMark(Mark mark, bool inline, SerializeOptions options)
    {
        try
        {
            Func<Mark, bool, DomOutputSpec> toDom = Marks.Single(x => x.Key == mark.Type.Name).Value;
            return Utils.RenderSpec(Utils.Doc(options.Document), toDom(mark, inline), mark.Attrs);
        }
        catch
        {
            return null;
        }
    }

    public static DomContent RenderSpec(HtmlDocument doc, DomOutputSpec structure)
    {
        return Utils.RenderSpec(doc, structure, null);
    }
    public static DomContent RenderSpec(HtmlDocument doc, DomOutputSpec structure, Attrs? blockArraysIn)
    {
        return Utils.RenderSpec(doc, structure, blockArraysIn);
    }

    static Dictionary<string, Func<Node, DomOutputSpec>> NodesFromSchema(Schema schema)
    {
        Dictionary<string, Func<Node, DomOutputSpec>> result = Utils.GatherToDom(schema.Nodes);

        if (!result.ContainsKey("text"))
        {
            result["text"] = node => new DomOutputSpec(node.Text ?? "");
        }

        return result;
    }
    
    static Dictionary<string, Func<Mark, bool, DomOutputSpec>> MarksFromSchema(Schema schema)
    {
        return Utils.GatherToDom(schema.Marks);
    }
}

public static class Utils
{
    private static readonly Dictionary<Attrs, List<JsonNode>?> SuspiciousAttributeCache = new();
    
    public static HtmlDocument Doc(HtmlDocument? document)
    {
        return document ?? new HtmlDocument();
    }

    public static Dictionary<string, Func<Node, DomOutputSpec>> GatherToDom(Dictionary<string, NodeType> nodes)
    {
        Dictionary<string, Func<Node, DomOutputSpec>> result = [];

        foreach (KeyValuePair<string, NodeType> kv in nodes)
        {
            Func<Node, DomOutputSpec>? toDom = kv.Value.Spec.ToDom;

            if (toDom is not null)
            {
                result.Add(kv.Key, toDom);
            }
        }

        return result;
    }
    public static Dictionary<string, Func<Mark, bool, DomOutputSpec>> GatherToDom(Dictionary<string, MarkType> marks)
    {
        Dictionary<string, Func<Mark, bool, DomOutputSpec>> result = [];

        foreach (KeyValuePair<string, MarkType> kv in marks)
        {
            Func<Mark, bool, DomOutputSpec>? toDom = kv.Value.Spec.ToDom;

            if (toDom is not null)
            {
                result.Add(kv.Key, toDom);
            }
        }

        return result;
    }

    public static DomContent RenderSpec(HtmlDocument doc, DomOutputSpec structure, Attrs? blockArraysIn)
    {
        if (!string.IsNullOrEmpty(structure.Text))
        {
            return new DomContent(doc.CreateTextNode(structure.Text));
        }

        if (structure.DomNode is { NodeType: HtmlNodeType.Element })
        {
            return new DomContent(structure.DomNode);
        }

        if (structure.DomContent is { Dom.NodeType: HtmlNodeType.Element })
        {
            return structure.DomContent;
        }

        if (structure.ElementArray is null)
        {
            throw new Exception("Invalid `structure` argument");
        }

        string tagName = structure.GetFirstArrayValue();

        List<JsonNode>? suspicious = SuspiciousAttributes(blockArraysIn);
        
        if (blockArraysIn is not null &&
            suspicious is not null &&
            suspicious.Any(sus => structure.ElementArray.Any(str => str.Attributes == sus)))
        {
            throw new Exception("Using an array from an attribute object as a DOM spec. This may be an attempted cross site scripting attack.");
        }

        // Skip Namespace config, can't be done without a browser. Throw error instead.
        if (tagName.Contains(' '))
        {
            throw new Exception("Tag names cannot have a space in them and XML Namespaces are not supported outside of a browser context.");
        }

        HtmlNode? contentDom = null;

        HtmlNode dom = doc.CreateElement(tagName);

        int start = 1;
        
        JsonNode? attrs = structure.ElementArray[1].Attributes;

        if (attrs is not null && attrs.GetValueKind() == JsonValueKind.Object && attrs["nodeType"] is null)
        {
            start = 2;
            
            foreach (KeyValuePair<string, JsonNode?> attribute in attrs.AsObject())
            {
                if (string.IsNullOrEmpty(attribute.Key)) continue;

                if (attribute.Key.Contains(' '))
                {
                    throw new Exception("Attribute names cannot have a space in them and XML Namespaces are not supported outside of a browser context.");
                }

                dom.SetAttributeValue(attribute.Key, (attribute.Value?.AsValue().ToString()) ?? "");
            }
        }

        for (int i = start; i < structure.ElementArray.Count; i++)
        {
            JsonNode? child = structure.ElementArray[i].Attributes;

            if (child is not null && child.AsValue().TryGetValue(out int val) && val == 0)
            {
                if (i < structure.ElementArray.Count - 1 || i > start)
                {
                    throw new Exception("Content hole must be the only child of its parent node");
                }

                return new DomContent(dom, dom);
            }

            if (structure.ElementArray[i].Child is null)
            {
                throw new Exception("Error with array value");
            }

            DomContent inner = RenderSpec(doc, structure.ElementArray[i].Child!, blockArraysIn);

            dom.AppendChild(inner.Dom);

            if (inner.Content is null) continue;

            if (contentDom is not null)
            {
                throw new Exception("Multiple content holes");
            }

            contentDom = inner.Content;
        }

        return new DomContent(dom, contentDom);
    }
    
    private static List<JsonNode>? SuspiciousAttributes(Attrs? attrs)
    {
        if (attrs is null) return null;
        
        if(!SuspiciousAttributeCache.TryGetValue(attrs, out List<JsonNode>? value))
        {
            value = SuspiciousAttributesInner(attrs);
            SuspiciousAttributeCache.Add(attrs, value);
        }
    
        return value;
    }
    
    private static List<JsonNode>? SuspiciousAttributesInner(Attrs attrs)
    {
        List<JsonNode>? result = null;
    
        void Scan(JsonNode? value)
        {
            if (value is not JsonArray) return;
            
            if (value.AsArray()[0]?.GetValueKind() == JsonValueKind.String)
            {
                result ??= [];
                result.Add(value);
            }
            else
            {
                for (int i = 0; i < value.AsArray().Count; i++)
                {
                    Scan(value.AsArray()[i]);
                }
            }
        }
        
        foreach (KeyValuePair<string,JsonNode?> kv in attrs)
        {
            Scan(kv.Value);
        }

        return result;
    }
}

public class DomOutputSpec
{
    public string? Text { get; set; }
    public List<ElementSpec>? ElementArray { get; set; }
    public HtmlNode? DomNode { get; set; }
    public DomContent? DomContent { get; set; }

    public DomOutputSpec(string value)
    {
        Text = value;
    }

    public DomOutputSpec(List<ElementSpec> elementArray)
    {
        ElementArray = elementArray;
    }

    public DomOutputSpec(HtmlNode domNode)
    {
        DomNode = domNode;
    }

    public DomOutputSpec(DomContent domContent)
    {
        DomContent = domContent;
    }

    public string GetFirstArrayValue()
    {
        return (ElementArray ?? throw new InvalidOperationException("Invalid array")).First().TagName ?? throw new InvalidOperationException("Invalid array, first entry is not a string");
    }
}

public class DomContent(HtmlNode dom, HtmlNode? content = null)
{
    public HtmlNode Dom = dom;
    public HtmlNode? Content = content;
}

public class ElementSpec
{
    public string? TagName { get; set; }
    public JsonNode? Attributes { get; set; }
    public DomOutputSpec? Child { get; set; }

    public ElementSpec(string tagName)
    {
        TagName = tagName;
    }
    
    public ElementSpec(JsonNode attributes)
    {
        Attributes = attributes;
    }
    
    public ElementSpec(DomOutputSpec child)
    {
        Child = child;
    }
}