// [UAA] - START - xchen tonemap (overwrites Reinhard); sliders: contrast, gamma, huePreserve, saturation
#version 330
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

struct PS_Input
{
    vec4 Pos;
    vec2 UV;
};

struct PS_ConstantBuffer
{
    vec4 g_toneparams;     // [UAA] { exposure, contrast, gamma, huePreserve }
    vec4 g_toneparams2;    // [UAA] { saturation, _, _, _ }
};

uniform PS_ConstantBuffer CBPS0;

layout(binding = 0) uniform sampler2D Sampler_g_sampler;

in vec2 _VSPS_UV;
layout(location = 0) out vec4 _entryPointOutput;

// [UAA] ported from user's private xchen reference curve
float tonemapChen(float x)
{
    const float Cs = 3.333;
    const float Ct = 0.37;
    const float Cl = 0.517;
    const float Ce = 0.8;

    float ts = x * smoothstep(0.0, 1.0, x * Cs);
    float x1 = x * (1.0 - Ct) + ts * Ct;

    if (x1 > Cl)
    {
        float d = x1 - Cl;
        x1 = d / (1.0 + d) + Cl;
    }

    if (x1 > Ce)
    {
        float y = (x1 - Ce) / (1.0 - Ce);
        x1 = Ce + (1.0 - exp(-y)) * (1.0 - Ce);
    }

    return x1;
}

// [UAA] Rec.709 luma weights (Effekseer composites in sRGB linear; NOT Rec.2020)
vec3 tonemapChenHuePreserve(vec3 c, float hueAmount)
{
    const vec3 LUMA709 = vec3(0.2126, 0.7152, 0.0722);
    float luma = max(dot(c, LUMA709), 1e-5);
    vec3 hue = c / luma;
    vec3 tonemapHueShifted = vec3(
        tonemapChen(c.r),
        tonemapChen(c.g),
        tonemapChen(c.b));
    vec3 tonemapHuePreserved = hue * tonemapChen(luma);
    return mix(tonemapHueShifted, tonemapHuePreserved, hueAmount);
}

vec4 _main(PS_Input Input)
{
    vec3 texel = texture(Sampler_g_sampler, Input.UV).xyz;

    // Exposure
    texel *= CBPS0.g_toneparams.x;

    // [UAA] AE contrast: pivot-pow around 0.18
    const float pivot = 0.18;
    texel = pow(max(texel / pivot, 1e-5), CBPS0.g_toneparams.y) * pivot;

    // xchen tonemap with hue preservation
    texel = tonemapChenHuePreserve(texel, CBPS0.g_toneparams.w);

    // [UAA] Saturation: lerp toward Rec.709 luma
    float displayLuma = dot(texel, vec3(0.2126, 0.7152, 0.0722));
    texel = clamp(mix(vec3(displayLuma), texel, CBPS0.g_toneparams2.x), 0.0, 1.0);

    // [UAA] Gamma: pow
    texel = pow(max(texel, 0.0), CBPS0.g_toneparams.z);

    return vec4(texel, 1.0);
}

void main()
{
    PS_Input Input;
    Input.Pos = gl_FragCoord;
    Input.UV = _VSPS_UV;
    _entryPointOutput = _main(Input);
}
// [UAA] - END
