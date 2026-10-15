float _ImageSliced;
float4 _SliceBorder, _SliceUVBorder;

float GlassSliceAxis(float value, float2 destinationBorder, float2 sourceBorder)
{
    if (value < destinationBorder.x)
        return value * sourceBorder.x / max(destinationBorder.x, 0.000001);
    if (value > 1 - destinationBorder.y)
        return 1 - (1 - value) * sourceBorder.y / max(destinationBorder.y, 0.000001);
    float center = (value - destinationBorder.x) / max(1 - destinationBorder.x - destinationBorder.y, 0.000001);
    return sourceBorder.x + center * max(0, 1 - sourceBorder.x - sourceBorder.y);
}

float2 GlassSliceUV(float2 uv)
{
    return float2(GlassSliceAxis(uv.x, _SliceBorder.xz, _SliceUVBorder.xz),
        GlassSliceAxis(uv.y, _SliceBorder.yw, _SliceUVBorder.yw));
}
