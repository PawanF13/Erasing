using ComputeSharp;

[ThreadGroupSize(8, 8, 1)]
[GeneratedComputeShaderDescriptor]
public partial struct EraseShader : IComputeShader
{
    public ReadWriteTexture2D<float4> texture;
    public Float2 eraseCenter;
    public float eraseRadius;
    public float feather; // e.g. 1.0–5.0 for soft edge pixels

    public void Execute()
    {
        Float2 pos = (Float2)ThreadIds.XY;
        float distance = Hlsl.Length(pos - eraseCenter);

        float innerRadius = eraseRadius - feather;
        float outerRadius = eraseRadius;

        if (distance < innerRadius)
        {
            // Fully erase inside the inner radius
            texture[ThreadIds.XY] = 0;
        }
        else if (distance < outerRadius)
        {
            float t = Hlsl.Saturate((distance - innerRadius) / (outerRadius - innerRadius));
            float eraseFactor = 1.0f - t; // Erase factor from 1 (center) to 0 (edge)

            float4 color = texture[ThreadIds.XY];
            color *= 1.0f - eraseFactor; // Apply erase to all channels
            texture[ThreadIds.XY] = color;
        }
        // else: outside radius, do nothing
    }
}
