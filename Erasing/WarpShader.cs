using ComputeSharp;

[ThreadGroupSize(8, 8, 1)]
[GeneratedComputeShaderDescriptor]
public partial struct WarpShader : IComputeShader
{
    public ReadOnlyBuffer<Int2> controlPoints;
    public ReadOnlyBuffer<Int2> originalPoints;
    public ReadWriteTexture2D<float4> texture;
    public ReadOnlyTexture2D<float4> sourceTexture;
    public float scale;

    public int imageWidth;
    public int imageHeight;

    public WarpShader(
        ReadOnlyBuffer<Int2> controlPoints,
        ReadOnlyBuffer<Int2> originalPoints,
        ReadWriteTexture2D<float4> texture,
        ReadOnlyTexture2D<float4> sourceTexture,
        float scale,
        int imageWidth,
        int imageHeight)
    {
        this.controlPoints = controlPoints;
        this.originalPoints = originalPoints;
        this.texture = texture;
        this.sourceTexture = sourceTexture;
        this.scale = scale;
        this.imageWidth = imageWidth;
        this.imageHeight = imageHeight;
    }


    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        int width = texture.Width;
        int height = texture.Height;

        float u = (float)x / (width - 1);
        float v = (float)y / (height - 1);

        float2 warped = BicubicInterpolate_ControlPoints(u, v);
        float2 original = BicubicInterpolate_OriginalPoints(u, v);

        float2 delta = warped - original;
        float2 source = new float2(x, y) - (delta / scale);

        Int2 samplePos = (Int2)(int2)(Hlsl.Clamp(source, new float2(0, 0), new float2(width - 1, height - 1)) + 0.5f);
        texture[x, y] = sourceTexture[samplePos];
    }

    private float2 BicubicInterpolate_ControlPoints(float u, float v)
    {
        float uPos = u * 2.0f;
        float vPos = v * 2.0f;
        int baseX = (int)Hlsl.Floor(uPos);
        int baseY = (int)Hlsl.Floor(vPos);
        float uFrac = uPos - baseX;
        float vFrac = vPos - baseY;

        float4 InterpolateRowX = new float4();
        float4 InterpolateRowY = new float4();

        for (int j = -1; j <= 2; j++)
        {
            float4 px = new float4();
            float4 py = new float4();
            for (int i = -1; i <= 2; i++)
            {
                int ix = Hlsl.Clamp(baseX + i, 0, 2);
                int iy = Hlsl.Clamp(baseY + j, 0, 2);
                Int2 pt = controlPoints[iy * 3 + ix];
                px[i + 1] = pt.X;
                py[i + 1] = pt.Y;
            }

            InterpolateRowX[j + 1] = Cubic(px[0], px[1], px[2], px[3], uFrac);
            InterpolateRowY[j + 1] = Cubic(py[0], py[1], py[2], py[3], uFrac);
        }

        float x = Cubic(InterpolateRowX[0], InterpolateRowX[1], InterpolateRowX[2], InterpolateRowX[3], vFrac);
        float y = Cubic(InterpolateRowY[0], InterpolateRowY[1], InterpolateRowY[2], InterpolateRowY[3], vFrac);

        return new float2(x, y);
    }

    private float2 BicubicInterpolate_OriginalPoints(float u, float v)
    {
        float uPos = u * 2.0f;
        float vPos = v * 2.0f;
        int baseX = (int)Hlsl.Floor(uPos);
        int baseY = (int)Hlsl.Floor(vPos);
        float uFrac = uPos - baseX;
        float vFrac = vPos - baseY;

        float4 InterpolateRowX = new float4();
        float4 InterpolateRowY = new float4();

        for (int j = -1; j <= 2; j++)
        {
            float4 px = new float4();
            float4 py = new float4();
            for (int i = -1; i <= 2; i++)
            {
                int ix = Hlsl.Clamp(baseX + i, 0, 2);
                int iy = Hlsl.Clamp(baseY + j, 0, 2);
                Int2 pt = originalPoints[iy * 3 + ix];
                px[i + 1] = pt.X;
                py[i + 1] = pt.Y;
            }

            InterpolateRowX[j + 1] = Cubic(px[0], px[1], px[2], px[3], uFrac);
            InterpolateRowY[j + 1] = Cubic(py[0], py[1], py[2], py[3], uFrac);
        }

        float x = Cubic(InterpolateRowX[0], InterpolateRowX[1], InterpolateRowX[2], InterpolateRowX[3], vFrac);
        float y = Cubic(InterpolateRowY[0], InterpolateRowY[1], InterpolateRowY[2], InterpolateRowY[3], vFrac);

        return new float2(x, y);
    }

    private float Cubic(float p0, float p1, float p2, float p3, float t)
    {
        return p1 + 0.5f * t * (p2 - p0 +
               t * (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3 +
               t * (3.0f * (p1 - p2) + p3 - p0)));
    }
}
