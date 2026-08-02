
#ifndef __EFFEKSEER_MATERIAL_H__
#define __EFFEKSEER_MATERIAL_H__

#include "../Effekseer.Base.Pre.h"
#include "../Parameter/Effekseer.Parameters.h"
#include "../Utils/Effekseer.BinaryVersion.h"
#include <array>
#include <assert.h>
#include <map>
#include <sstream>
#include <string.h>
#include <vector>

namespace Effekseer
{

class MaterialFile
{
public:
	enum class RequiredPredefinedMethodType : int32_t
	{
		Gradient = 0,
		Noise = 1,
		Light = 2,
		LocalTime = 3,
		Hsv = 4,
		ParticleTime = 5,
	};

	struct GradientParameter
	{
		std::string Name;
		Gradient Data;
	};

private:
	const int32_t customDataMinCount_ = 2;

	struct Texture
	{
		std::string Name;
		int32_t Index;
		TextureWrapType Wrap;
		TextureColorType ColorType;
		// [UAA] - START - authoring metadata the file already stores but upstream discards
		std::string HumanNameUAA;
		std::string DefaultPathUAA;
		// [UAA] - END
	};

	struct Uniform
	{
		std::string Name;
		int32_t Index;
		std::array<float, 4> DefaultValueUAA{}; // [UAA]
	};

	// [UAA] - START - custom data defaults, carried in the E_CD chunk upstream ignores
	struct CustomDataUAA
	{
		std::array<float, 4> DefaultValueUAA{};
	};
	// [UAA] - END

	uint64_t guid_ = 0;

	std::string genericCode_;

	bool hasRefraction_ = false;

	bool isSimpleVertex_ = false;

	ShadingModelType shadingModel_;

	int32_t customData1Count_ = 0;
	int32_t customData2Count_ = 0;

	std::vector<Texture> textures_;

	std::vector<Uniform> uniforms_;

	std::vector<CustomDataUAA> customDataUAA_; // [UAA]

	static constexpr int32_t LatestSupportVersion = MaterialVersion18; // [UAA] - implicitly inline under C++17 so the BinaryReader::Read const-ref bounds below do not require an out-of-line definition
	static constexpr int32_t OldestSupportVersion = 0; // [UAA] - see LatestSupportVersion

public:
	std::vector<GradientParameter> Gradients;

	std::vector<GradientParameter> FixedGradients;

	std::vector<RequiredPredefinedMethodType> RequiredMethods;

	MaterialFile() = default;
	virtual ~MaterialFile() = default;

	virtual bool Load(const uint8_t* data, int32_t size);

	virtual ShadingModelType GetShadingModel() const;

	virtual void SetShadingModel(ShadingModelType shadingModel);

	virtual bool GetIsSimpleVertex() const;

	virtual void SetIsSimpleVertex(bool isSimpleVertex);

	virtual bool GetHasRefraction() const;

	virtual void SetHasRefraction(bool hasRefraction);

	virtual const char* GetGenericCode() const;

	virtual void SetGenericCode(const char* code);

	virtual uint64_t GetGUID() const;

	virtual void SetGUID(uint64_t guid);

	virtual TextureColorType GetTextureColorType(int32_t index) const;

	virtual TextureWrapType GetTextureWrap(int32_t index) const;

	virtual void SetTextureWrap(int32_t index, TextureWrapType value);

	virtual int32_t GetTextureIndex(int32_t index) const;

	virtual void SetTextureIndex(int32_t index, int32_t value);

	virtual const char* GetTextureName(int32_t index) const;

	virtual void SetTextureName(int32_t index, const char* name);

	virtual int32_t GetTextureCount() const;

	virtual void SetTextureCount(int32_t count);

	virtual int32_t GetUniformIndex(int32_t index) const;

	virtual void SetUniformIndex(int32_t index, int32_t value);

	virtual const char* GetUniformName(int32_t index) const;

	virtual void SetUniformName(int32_t index, const char* name);

	virtual int32_t GetUniformCount() const;

	virtual void SetUniformCount(int32_t count);

	virtual int32_t GetCustomData1Count() const;

	virtual void SetCustomData1Count(int32_t count);

	virtual int32_t GetCustomData2Count() const;

	virtual void SetCustomData2Count(int32_t count);

	// [UAA] - START - accessors for authored names, paths and default values
	virtual const char* GetTextureHumanNameUAA(int32_t index) const;

	virtual const char* GetTextureDefaultPathUAA(int32_t index) const;

	virtual std::array<float, 4> GetUniformDefaultValueUAA(int32_t index) const;

	virtual int32_t GetCustomDataDefaultCountUAA() const;

	virtual std::array<float, 4> GetCustomDataDefaultValueUAA(int32_t index) const;
	// [UAA] - END
};

} // namespace Effekseer

#endif // __EFFEKSEER_MATERIAL_H__
