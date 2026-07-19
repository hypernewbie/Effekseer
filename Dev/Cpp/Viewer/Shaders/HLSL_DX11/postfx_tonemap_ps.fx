// [UAA] - START - xchen tonemap (overwrites Reinhard); mirrors ToolRuntime copy
Texture2D g_texture : register(t0);
SamplerState g_sampler : register(s0);

cbuffer PS_ConstantBuffer : register(b0)
{
	float4 g_toneparams;
};

struct PS_Input
{
	float4 Pos : SV_POSITION;
	float2 UV : TEXCOORD0;
};

// Ported from user's private xchen reference curve
float tonemapChen(float x)
{
	const float Cs = 3.333f;
	const float Ct = 0.37f;
	const float Cl = 0.517f;
	const float Ce = 0.8f;

	float ts = x * smoothstep(0.0f, 1.0f, x * Cs);
	float x1 = x * (1.0f - Ct) + ts * Ct;

	if (x1 > Cl)
	{
		float d = x1 - Cl;
		x1 = d / (1.0f + d) + Cl;
	}

	if (x1 > Ce)
	{
		float y = (x1 - Ce) / (1.0f - Ce);
		x1 = Ce + (1.0f - exp(-y)) * (1.0f - Ce);
	}

	return x1;
}

// [UAA] Rec.709 luma weights
float3 tonemapChenHuePreserve(float3 c)
{
	const float3 LUMA709 = float3(0.2126f, 0.7152f, 0.0722f);
	float luma = max(dot(c, LUMA709), 1e-5f);
	float3 hue = c / luma;
	float3 tonemapHueShifted = float3(
		tonemapChen(c.r),
		tonemapChen(c.g),
		tonemapChen(c.b));
	float3 tonemapHuePreserved = hue * tonemapChen(luma);
	// [UAA] v1: huePreserveAmount hardcoded to 0.2f (authored default)
	return lerp(tonemapHueShifted, tonemapHuePreserved, 0.2f);
}

float4 main(const PS_Input Input) : SV_Target
{
	float3 texel = g_texture.Sample(g_sampler, Input.UV).rgb * g_toneparams.x;
	return float4(tonemapChenHuePreserve(texel), 1.0f);
}
// [UAA] - END
