using PaintDotNet.Effects;

namespace ObjectRemoverPlugin;

/// <summary>Everything the dialog hands to the renderer. The Mask array is treated as immutable.</summary>
public sealed class ObjectRemoverToken : EffectConfigToken
{
    /// <summary>MaskWidth*MaskHeight bytes; non-zero = pixel to remove.</summary>
    public byte[]? Mask;
    public int MaskWidth;
    public int MaskHeight;

    /// <summary>Bounding box of the painted mask, [X0,X1) x [Y0,Y1).</summary>
    public int BoundsX0, BoundsY0, BoundsX1, BoundsY1;

    /// <summary>Changes whenever the mask is edited.</summary>
    public Guid MaskRevision = Guid.Empty;

    /// <summary>False while the user is still painting: the renderer then shows the untouched image.</summary>
    public bool RunEnabled;

    public ModelKind Model = ModelKind.Lama;
    public ProcessorMode Processor = ProcessorMode.Auto;
    public int GpuAdapter;
    public int ExpandPx = 6;
    public int FeatherPx = 4;
    public int BrushSize = 40;

    public ObjectRemoverToken()
    {
    }

    private ObjectRemoverToken(ObjectRemoverToken other)
    {
        Mask = other.Mask;
        MaskWidth = other.MaskWidth;
        MaskHeight = other.MaskHeight;
        BoundsX0 = other.BoundsX0;
        BoundsY0 = other.BoundsY0;
        BoundsX1 = other.BoundsX1;
        BoundsY1 = other.BoundsY1;
        MaskRevision = other.MaskRevision;
        RunEnabled = other.RunEnabled;
        Model = other.Model;
        Processor = other.Processor;
        GpuAdapter = other.GpuAdapter;
        ExpandPx = other.ExpandPx;
        FeatherPx = other.FeatherPx;
        BrushSize = other.BrushSize;
    }

    public bool HasMask => Mask != null && BoundsX1 > BoundsX0 && BoundsY1 > BoundsY0;

    public string RunKey() => MakeRunKey(MaskRevision, Model, Processor, GpuAdapter, ExpandPx, FeatherPx);

    public static string MakeRunKey(Guid revision, ModelKind model, ProcessorMode proc, int adapter, int expand, int feather) =>
        $"{revision:N}|{(int)model}|{(int)proc}|{adapter}|{expand}|{feather}";

    public override object Clone() => new ObjectRemoverToken(this);
}
