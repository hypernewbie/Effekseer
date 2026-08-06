// [UAA] - START - efkmatc: headless CLI over the Effekseer material system
// Exposes the material node editor's data without a display, for asset
// pipelines and CI. Subcommands:
//   info     parameter interface via Effekseer::MaterialFile (uniforms,
//            textures, custom data, and the authored defaults exposed by the
//            *UAA accessors), optionally as JSON
//   graph    editor-side node graph, as written by Material::SaveAsStr
//   validate load the graph and report per-node warnings
//   export   generated shader source via TextExporter
//   compile  .efkmat -> .efkmatd compiled cache, driving the same shader
//            compiler shared libraries the editor loads at runtime
//   props    list every editable node property with its current value
//   set      write one node property, by node GUID and property name
//   set-texture / set-value  write a named TextureObjectParameter or ParameterN
//   retarget rewrite texture path prefixes across the whole graph
//   from-spec / save-spec   import and export the editor graph as JSON
//            (the raw payload from Material::SaveAsStr, optionally wrapped
//            in a {"schema_version":..,"kind":"graph",..} envelope)
//   add-texture   create a TextureObjectParameter node, leave it available
//                 for graph wiring
//   add-uniform   create a Parameter1..Parameter4 node with a default value,
//                 leave it available for graph wiring
//   set-custom-data  write one of the two Material::CustomData slots
//   set-shading-model  set the Output node's ShadingModel property
// Every write command rebuilds the graph, regenerates the shader code, and
// recomputes the GUID via Material::Save. The runtime chunks (PRM_, GENE,
// E_CD, DATA) are emitted by Material::Save from the graph itself; we never
// hand-derive them.
// Exit codes: 0 ok, 1 usage, 2 load failure, 3 validation warnings,
// 4 compile failure, 5 edit failure.
// [UAA] - END

#include <algorithm>
#include <cstdint>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <limits>
#include <string>
#include <vector>

#include "../Effekseer/Effekseer/Material/Effekseer.CompiledMaterial.h"
#include "../Effekseer/Effekseer/Material/Effekseer.MaterialFile.h"
#include "../Viewer/CompiledMaterialGenerator.h"

#if EFKMATC_ENABLE_RENDER
#include "Render.h"
#endif

#include "efkMat.Base.h"
#include "efkMat.Library.h"
#include "efkMat.Models.h"
#include "efkMat.Parameters.h"
#include "efkMat.TextExporter.h"
#include "ThirdParty/picojson.h"

namespace
{

constexpr int ExitOk = 0;
constexpr int ExitUsage = 1;
constexpr int ExitLoadFailure = 2;
constexpr int ExitWarnings = 3;
constexpr int ExitCompileFailure = 4;
constexpr int ExitEditFailure = 5;
constexpr int ExitRenderFailure = 6;

int Usage()
{
	std::cerr << "usage: efkmatc <command> [options]\n\n"
			  << "  info     FILE.efkmat [--json]\n"
			  << "  graph    FILE.efkmat\n"
			  << "  validate FILE.efkmat\n"
			  << "  export   FILE.efkmat -o OUTDIR\n"
			  << "  compile  FILE.efkmat [-o OUT.efkmatd] [--tools-dir DIR] [--verify]\n"
			  << "  compile  --batch DIR [--tools-dir DIR] [--verify]\n"
			  << "\n"
			  << "  props       FILE.efkmat\n"
			  << "  set         FILE.efkmat --node GUID --prop NAME (--str S | --value a,b,c,d) OUT\n"
			  << "  set-texture FILE.efkmat --param NAME --path PATH OUT\n"
			  << "  set-value   FILE.efkmat --param NAME --value a,b,c,d OUT\n"
			  << "  retarget    FILE.efkmat --from PREFIX --to PREFIX OUT\n"
			  << "\n"
			  << "  from-spec  SPEC.json -o OUT.efkmat\n"
			  << "  save-spec  FILE.efkmat -o OUTSPEC.json\n"
			  << "  add-texture     FILE.efkmat --name NAME --path PATH [-o OUT.efkmat]\n"
			  << "  add-uniform     FILE.efkmat --name NAME --default x[,y,z,w] [-o OUT.efkmat]\n"
			  << "  set-custom-data FILE.efkmat --slot 0|1 --default x,y,z,w [-o OUT.efkmat]\n"
			  << "  set-shading-model FILE.efkmat --shading-model lit|unlit [-o OUT.efkmat]\n"
			  << "\n"
			  << "  render      FILE.efkmat -o OUT.png [--model screen|sphere] [--time T]\n"
			  << "              [--resources DIR]\n"
			  << "\n"
			  << "  Editing writes to OUT (-o), or in place with --in-place. Editing changes\n"
			  << "  the material GUID, so recompile any .efkmatd cache afterwards.\n"
			  << "  render draws offscreen; it needs no window and no display server.\n";
	return ExitUsage;
}

bool ReadFile(const std::string& path, std::vector<uint8_t>& out)
{
	std::ifstream file(path, std::ios::binary);
	if (!file)
	{
		return false;
	}

	file.seekg(0, std::ios::end);
	const auto size = static_cast<std::streamoff>(file.tellg());
	file.seekg(0, std::ios::beg);
	if (size < 0)
	{
		return false;
	}

	out.resize(static_cast<size_t>(size));
	if (size > 0 && !file.read(reinterpret_cast<char*>(out.data()), size))
	{
		return false;
	}
	return true;
}

bool ReadTextFile(const std::string& path, std::string& out)
{
	std::ifstream file(path, std::ios::binary);
	if (!file)
	{
		return false;
	}

	file.seekg(0, std::ios::end);
	const auto size = static_cast<std::streamoff>(file.tellg());
	file.seekg(0, std::ios::beg);
	if (size < 0)
	{
		return false;
	}

	out.resize(static_cast<size_t>(size));
	if (size > 0 && !file.read(out.data(), size))
	{
		return false;
	}
	return true;
}

// Path conventions, which differ between the two APIs used here and are easy to
// get wrong:
//
//  * EffekseerMaterial's PathHelper::Absolute/Relative pop the LAST component of
//    the base unless it ends in a slash, i.e. they expect the material FILE path.
//    Material::Load/Save/SaveAsStr therefore take the file path, exactly as the
//    editor passes it. Handing them a directory silently anchors one level too
//    high.
//  * The std::filesystem helpers below need a real directory.

//! Absolute directory holding a material file, for the std::filesystem helpers.
std::string BaseDirectory(const std::string& path)
{
	std::error_code ec;
	auto absolute = std::filesystem::absolute(std::filesystem::path(path), ec);
	if (ec)
	{
		return std::filesystem::path(path).parent_path().string();
	}
	return absolute.parent_path().lexically_normal().string();
}

//! Texture paths live in memory as absolute paths and are written relative to the
//! material file. These two helpers convert between the two views so the CLI can
//! speak the stored (relative) form to the user while storing what Save expects.
std::string StoredPath(const std::string& absolutePath, const std::string& baseDirectory)
{
	if (absolutePath.empty())
	{
		return absolutePath;
	}
	std::error_code ec;
	const auto relative = std::filesystem::relative(absolutePath, baseDirectory, ec);
	if (ec || relative.empty())
	{
		return absolutePath;
	}
	return relative.generic_string();
}

std::string ResolvePath(const std::string& userPath, const std::string& baseDirectory)
{
	if (userPath.empty())
	{
		return userPath;
	}
	std::filesystem::path candidate(userPath);
	if (!candidate.is_absolute())
	{
		candidate = std::filesystem::path(baseDirectory) / candidate;
	}
	return candidate.lexically_normal().generic_string();
}

std::string JsonEscape(const std::string& value)
{
	std::string result;
	result.reserve(value.size() + 8);
	for (const char c : value)
	{
		switch (c)
		{
		case '"': result += "\\\""; break;
		case '\\': result += "\\\\"; break;
		case '\n': result += "\\n"; break;
		case '\r': result += "\\r"; break;
		case '\t': result += "\\t"; break;
		default:
			if (static_cast<unsigned char>(c) < 0x20)
			{
				char buffer[8];
				snprintf(buffer, sizeof(buffer), "\\u%04x", static_cast<unsigned char>(c));
				result += buffer;
			}
			else
			{
				result += c;
			}
			break;
		}
	}
	return result;
}

std::string Floats4(const std::array<float, 4>& values)
{
	std::string result = "[";
	for (size_t i = 0; i < values.size(); i++)
	{
		result += (i == 0 ? "" : ", ") + std::to_string(values[i]);
	}
	return result + "]";
}

const char* WarningName(EffekseerMaterial::WarningType type)
{
	switch (type)
	{
	case EffekseerMaterial::WarningType::None: return "None";
	case EffekseerMaterial::WarningType::WrongInputType: return "WrongInputType";
	case EffekseerMaterial::WarningType::WrongProperty: return "WrongProperty";
	case EffekseerMaterial::WarningType::DifferentSampler: return "DifferentSampler";
	case EffekseerMaterial::WarningType::InvalidName: return "InvalidName";
	case EffekseerMaterial::WarningType::SameName: return "SameName";
	case EffekseerMaterial::WarningType::PixelNodeAndNormal: return "PixelNodeAndNormal";
	}
	return "Unknown";
}

//! Load the editor-side node graph. Returns nullptr on failure.
std::shared_ptr<EffekseerMaterial::Material> LoadGraph(const std::string& path)
{
	std::vector<uint8_t> data;
	if (!ReadFile(path, data))
	{
		std::cerr << "efkmatc: cannot read " << path << "\n";
		return nullptr;
	}

	auto library = std::make_shared<EffekseerMaterial::Library>();
	auto material = std::make_shared<EffekseerMaterial::Material>();
	material->Initialize();

	// Pass the file path: PathHelper strips the last component itself.
	const auto error = material->Load(data, library, path.c_str());
	if (error != EffekseerMaterial::ErrorCode::OK)
	{
		std::cerr << "efkmatc: failed to load graph (ErrorCode=" << static_cast<int>(error) << ") " << path << "\n";
		return nullptr;
	}
	return material;
}

std::shared_ptr<EffekseerMaterial::Node> FindOutputNode(const std::shared_ptr<EffekseerMaterial::Material>& material)
{
	for (const auto& node : material->GetNodes())
	{
		if (node->Parameter->Type == EffekseerMaterial::NodeType::Output)
		{
			return node;
		}
	}
	return nullptr;
}

int CommandInfo(const std::string& path, bool asJson)
{
	std::vector<uint8_t> data;
	if (!ReadFile(path, data))
	{
		std::cerr << "efkmatc: cannot read " << path << "\n";
		return ExitLoadFailure;
	}

	Effekseer::MaterialFile file;
	if (!file.Load(data.data(), static_cast<int32_t>(data.size())))
	{
		std::cerr << "efkmatc: not a loadable material file: " << path << "\n";
		return ExitLoadFailure;
	}

	const int32_t textureCount = file.GetTextureCount();
	const int32_t uniformCount = file.GetUniformCount();

	if (asJson)
	{
		std::cout << "{\n";
		std::cout << "  \"guid\": " << file.GetGUID() << ",\n";
		std::cout << "  \"shadingModel\": " << static_cast<int>(file.GetShadingModel()) << ",\n";
		std::cout << "  \"hasRefraction\": " << (file.GetHasRefraction() ? "true" : "false") << ",\n";
		std::cout << "  \"customData1Count\": " << file.GetCustomData1Count() << ",\n";
		std::cout << "  \"customData2Count\": " << file.GetCustomData2Count() << ",\n";

		std::cout << "  \"textures\": [\n";
		for (int32_t i = 0; i < textureCount; i++)
		{
			std::cout << "    { \"name\": \"" << JsonEscape(file.GetTextureName(i)) << "\""
					  << ", \"humanName\": \"" << JsonEscape(file.GetTextureHumanNameUAA(i)) << "\""
					  << ", \"defaultPath\": \"" << JsonEscape(file.GetTextureDefaultPathUAA(i)) << "\""
					  << ", \"index\": " << file.GetTextureIndex(i)
					  << ", \"colorType\": " << static_cast<int>(file.GetTextureColorType(i))
					  << ", \"wrap\": " << static_cast<int>(file.GetTextureWrap(i)) << " }"
					  << (i + 1 < textureCount ? "," : "") << "\n";
		}
		std::cout << "  ],\n";

		std::cout << "  \"uniforms\": [\n";
		for (int32_t i = 0; i < uniformCount; i++)
		{
			std::cout << "    { \"name\": \"" << JsonEscape(file.GetUniformName(i)) << "\""
					  << ", \"index\": " << file.GetUniformIndex(i)
					  << ", \"default\": " << Floats4(file.GetUniformDefaultValueUAA(i)) << " }"
					  << (i + 1 < uniformCount ? "," : "") << "\n";
		}
		std::cout << "  ],\n";

		const int32_t customDefaults = file.GetCustomDataDefaultCountUAA();
		std::cout << "  \"customDataDefaults\": [\n";
		for (int32_t i = 0; i < customDefaults; i++)
		{
			std::cout << "    " << Floats4(file.GetCustomDataDefaultValueUAA(i))
					  << (i + 1 < customDefaults ? "," : "") << "\n";
		}
		std::cout << "  ]\n";
		std::cout << "}\n";
		return ExitOk;
	}

	std::cout << "guid              " << file.GetGUID() << "\n";
	std::cout << "shadingModel      " << static_cast<int>(file.GetShadingModel()) << "\n";
	std::cout << "hasRefraction     " << (file.GetHasRefraction() ? "yes" : "no") << "\n";
	std::cout << "customData1Count  " << file.GetCustomData1Count() << "\n";
	std::cout << "customData2Count  " << file.GetCustomData2Count() << "\n";
	std::cout << "genericCodeBytes  " << strlen(file.GetGenericCode()) << "\n";

	std::cout << "textures          " << textureCount << "\n";
	for (int32_t i = 0; i < textureCount; i++)
	{
		std::cout << "  [" << i << "] name=" << file.GetTextureName(i)
				  << " humanName=" << file.GetTextureHumanNameUAA(i)
				  << " defaultPath=" << file.GetTextureDefaultPathUAA(i)
				  << " index=" << file.GetTextureIndex(i)
				  << " colorType=" << static_cast<int>(file.GetTextureColorType(i))
				  << " wrap=" << static_cast<int>(file.GetTextureWrap(i)) << "\n";
	}

	std::cout << "uniforms          " << uniformCount << "\n";
	for (int32_t i = 0; i < uniformCount; i++)
	{
		std::cout << "  [" << i << "] name=" << file.GetUniformName(i)
				  << " index=" << file.GetUniformIndex(i)
				  << " default=" << Floats4(file.GetUniformDefaultValueUAA(i)) << "\n";
	}

	const int32_t customDefaults = file.GetCustomDataDefaultCountUAA();
	std::cout << "customDataDefault " << customDefaults << "\n";
	for (int32_t i = 0; i < customDefaults; i++)
	{
		std::cout << "  [" << i << "] " << Floats4(file.GetCustomDataDefaultValueUAA(i)) << "\n";
	}
	return ExitOk;
}

const char* ValueTypeName(EffekseerMaterial::ValueType type)
{
	switch (type)
	{
	case EffekseerMaterial::ValueType::Float1: return "Float1";
	case EffekseerMaterial::ValueType::Float2: return "Float2";
	case EffekseerMaterial::ValueType::Float3: return "Float3";
	case EffekseerMaterial::ValueType::Float4: return "Float4";
	case EffekseerMaterial::ValueType::FloatN: return "FloatN";
	case EffekseerMaterial::ValueType::Bool: return "Bool";
	case EffekseerMaterial::ValueType::Texture: return "Texture";
	case EffekseerMaterial::ValueType::String: return "String";
	case EffekseerMaterial::ValueType::Function: return "Function";
	case EffekseerMaterial::ValueType::Enum: return "Enum";
	case EffekseerMaterial::ValueType::Int: return "Int";
	case EffekseerMaterial::ValueType::Gradient: return "Gradient";
	case EffekseerMaterial::ValueType::Unknown: return "Unknown";
	}
	return "Unknown";
}

//! True for property types whose value lives in NodeProperty::Str.
bool IsStringProperty(EffekseerMaterial::ValueType type)
{
	return type == EffekseerMaterial::ValueType::String || type == EffekseerMaterial::ValueType::Texture;
}

bool ParseFloat4(const std::string& text, std::array<float, 4>& out, int* componentCount = nullptr)
{
	out.fill(0.0f);
	size_t index = 0;
	size_t start = 0;
	while (start < text.size())
	{
		if (index == out.size())
		{
			return false;
		}

		const size_t comma = text.find(',', start);
		const std::string field = text.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
		if (field.empty())
		{
			return false;
		}

		try
		{
			size_t consumed = 0;
			const float value = std::stof(field, &consumed);
			if (consumed != field.size() || !std::isfinite(value))
			{
				return false;
			}
			out[index++] = value;
		}
		catch (const std::exception&)
		{
			return false;
		}

		if (comma == std::string::npos)
		{
			if (componentCount != nullptr)
			{
				*componentCount = static_cast<int>(index);
			}
			return true;
		}
		start = comma + 1;
	}
	return false;
}

std::string DescribeProperty(const std::shared_ptr<EffekseerMaterial::Node>& node,
							 size_t index,
							 const std::string& baseDirectory)
{
	const auto& parameter = node->Parameter->Properties[index];
	const auto& value = node->Properties[index];
	if (parameter->Type == EffekseerMaterial::ValueType::Texture)
	{
		// Show the form actually stored in the file, not the resolved absolute path.
		return "\"" + StoredPath(value->Str, baseDirectory) + "\"";
	}
	if (IsStringProperty(parameter->Type))
	{
		return "\"" + value->Str + "\"";
	}
	return Floats4(value->Floats);
}

//! Persist an edited graph. Save() regenerates the shader code and rewrites the
//! GUID from the graph itself, so no caller-side bookkeeping is needed.
int SaveMaterial(const std::shared_ptr<EffekseerMaterial::Material>& material,
				 const std::string& sourcePath,
				 const std::string& destination)
{
	if (FindOutputNode(material) == nullptr)
	{
		std::cerr << "efkmatc: refusing to save a graph without an output node\n";
		return ExitEditFailure;
	}

	// Load resolved every texture path to absolute, and Save re-relativizes them
	// against the destination file, so the stored relative paths keep pointing at
	// the same textures from wherever the material lands.
	(void)sourcePath;
	std::vector<uint8_t> data;
	if (!material->Save(data, destination.c_str()) || data.empty())
	{
		std::cerr << "efkmatc: failed to serialize material\n";
		return ExitEditFailure;
	}

	const auto parent = std::filesystem::path(destination).parent_path();
	if (!parent.empty())
	{
		std::error_code ec;
		std::filesystem::create_directories(parent, ec);
	}

	std::ofstream out(destination, std::ios::binary | std::ios::trunc);
	if (!out)
	{
		std::cerr << "efkmatc: cannot write " << destination << "\n";
		return ExitEditFailure;
	}
	out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
	out.close();
	if (!out)
	{
		std::cerr << "efkmatc: failed while writing " << destination << "\n";
		return ExitEditFailure;
	}

	std::cout << "wrote " << destination << " (" << data.size()
			  << " bytes); GUID changed, recompile any .efkmatd cache\n";
	return ExitOk;
}

//! Resolve the destination for an editing subcommand.
bool ResolveEditTarget(const std::string& source,
					   const std::string& output,
					   bool inPlace,
					   std::string& destination)
{
	if (inPlace)
	{
		destination = source;
		return true;
	}
	if (output.empty())
	{
		std::cerr << "efkmatc: editing needs -o OUT.efkmat, or --in-place to overwrite the input\n";
		return false;
	}
	destination = output;
	return true;
}

int CommandProps(const std::string& path)
{
	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	for (const auto& node : material->GetNodes())
	{
		const auto& properties = node->Parameter->Properties;
		if (properties.empty())
		{
			continue;
		}

		std::cout << "node " << node->GUID << " " << node->Parameter->TypeName << "\n";
		for (size_t i = 0; i < properties.size() && i < node->Properties.size(); i++)
		{
			std::cout << "  " << properties[i]->Name << " (" << ValueTypeName(properties[i]->Type)
					  << ") = " << DescribeProperty(node, i, BaseDirectory(path)) << "\n";
		}
	}
	return ExitOk;
}

//! Apply one property write to a node, dispatching on the declared type.
bool ApplyProperty(const std::shared_ptr<EffekseerMaterial::Material>& material,
				   const std::shared_ptr<EffekseerMaterial::Node>& node,
				   const std::string& propertyName,
				   const std::string& stringValue,
				   const std::string& floatValue,
				   const std::string& baseDirectory)
{
	const int32_t index = node->Parameter->GetPropertyIndex(propertyName);
	if (index < 0 || static_cast<size_t>(index) >= node->Properties.size())
	{
		std::cerr << "efkmatc: node " << node->GUID << " (" << node->Parameter->TypeName
				  << ") has no property named " << propertyName << "\n";
		return false;
	}

	const auto& parameter = node->Parameter->Properties[static_cast<size_t>(index)];
	auto& property = node->Properties[static_cast<size_t>(index)];

	if (IsStringProperty(parameter->Type))
	{
		if (stringValue.empty() && floatValue.empty())
		{
			std::cerr << "efkmatc: property " << propertyName << " is "
					  << ValueTypeName(parameter->Type) << "; pass --str or --path\n";
			return false;
		}

		if (parameter->Type == EffekseerMaterial::ValueType::Texture)
		{
			// A texture path is given relative to the material file; store the
			// resolved absolute path so Save writes the intended relative path.
			const auto resolved = ResolvePath(stringValue, baseDirectory);
			if (!resolved.empty() && !std::filesystem::exists(resolved))
			{
				std::cerr << "efkmatc: warning: texture does not exist: " << resolved << "\n";
			}
			material->ChangeValue(property, resolved);
			return true;
		}

		material->ChangeValue(property, stringValue);
		return true;
	}

	if (floatValue.empty())
	{
		std::cerr << "efkmatc: property " << propertyName << " is " << ValueTypeName(parameter->Type)
				  << "; pass --value a[,b,c,d]\n";
		return false;
	}

	std::array<float, 4> floats{};
	if (!ParseFloat4(floatValue, floats))
	{
		std::cerr << "efkmatc: cannot parse --value " << floatValue << "\n";
		return false;
	}
	material->ChangeValue(property, floats);
	return true;
}

//! Find a parameter-style node (TextureObjectParameter, ParameterN, ...) whose
//! "Name" property matches. That name is what the runtime exposes as the
//! material's uniform/texture identity, so it is the natural handle for editing.
std::shared_ptr<EffekseerMaterial::Node> FindNamedParameterNode(
	const std::shared_ptr<EffekseerMaterial::Material>& material,
	const std::string& name,
	bool wantTexture)
{
	for (const auto& node : material->GetNodes())
	{
		const int32_t nameIndex = node->Parameter->GetPropertyIndex("Name");
		if (nameIndex < 0 || static_cast<size_t>(nameIndex) >= node->Properties.size())
		{
			continue;
		}
		if (node->Properties[static_cast<size_t>(nameIndex)]->Str != name)
		{
			continue;
		}

		const bool isTexture = node->Parameter->Type == EffekseerMaterial::NodeType::TextureObjectParameter;
		if (isTexture == wantTexture)
		{
			return node;
		}
	}
	return nullptr;
}

int CommandSet(const std::string& path,
			   const std::string& destination,
			   uint64_t nodeGuid,
			   const std::string& propertyName,
			   const std::string& stringValue,
			   const std::string& floatValue)
{
	if (nodeGuid == 0 || propertyName.empty())
	{
		std::cerr << "efkmatc: set needs --node GUID and --prop NAME\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto node = material->FindNode(nodeGuid);
	if (node == nullptr)
	{
		std::cerr << "efkmatc: no node with GUID " << nodeGuid << "\n";
		return ExitEditFailure;
	}
	if (!ApplyProperty(material, node, propertyName, stringValue, floatValue, BaseDirectory(destination)))
	{
		return ExitEditFailure;
	}

	std::cout << "node " << nodeGuid << " " << propertyName << " updated\n";
	return SaveMaterial(material, path, destination);
}

int CommandSetNamed(const std::string& path,
					const std::string& destination,
					const std::string& parameterName,
					bool wantTexture,
					const std::string& stringValue,
					const std::string& floatValue)
{
	if (parameterName.empty())
	{
		std::cerr << "efkmatc: needs --param NAME\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto node = FindNamedParameterNode(material, parameterName, wantTexture);
	if (node == nullptr)
	{
		std::cerr << "efkmatc: no " << (wantTexture ? "texture" : "value")
				  << " parameter named " << parameterName << " (try 'props')\n";
		return ExitEditFailure;
	}

	const std::string property = wantTexture ? "Texture" : "Value";
	if (!ApplyProperty(material, node, property, stringValue, floatValue, BaseDirectory(destination)))
	{
		return ExitEditFailure;
	}

	std::cout << "parameter " << parameterName << " (" << node->Parameter->TypeName << ", node "
			  << node->GUID << ") updated\n";
	return SaveMaterial(material, path, destination);
}

int CommandRetarget(const std::string& path,
					const std::string& destination,
					const std::string& fromPrefix,
					const std::string& toPrefix)
{
	if (fromPrefix.empty())
	{
		std::cerr << "efkmatc: retarget needs --from PREFIX (and --to PREFIX)\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	// Prefixes are matched against the stored (relative) form, which is what the
	// user sees in 'props' and in the file itself.
	const auto base = BaseDirectory(destination);
	int rewritten = 0;
	for (const auto& node : material->GetNodes())
	{
		const auto& properties = node->Parameter->Properties;
		for (size_t i = 0; i < properties.size() && i < node->Properties.size(); i++)
		{
			if (properties[i]->Type != EffekseerMaterial::ValueType::Texture)
			{
				continue;
			}
			auto& property = node->Properties[i];
			const std::string stored = StoredPath(property->Str, base);
			if (stored.empty() || stored.rfind(fromPrefix, 0) != 0)
			{
				continue;
			}

			const std::string updatedStored = toPrefix + stored.substr(fromPrefix.size());
			std::cout << "node " << node->GUID << " " << properties[i]->Name << ": " << stored << " -> "
					  << updatedStored << "\n";
			material->ChangeValue(property, ResolvePath(updatedStored, base));
			rewritten++;
		}
	}

	if (rewritten == 0)
	{
		std::cerr << "efkmatc: no texture path started with " << fromPrefix << "\n";
		return ExitEditFailure;
	}

	std::cout << "retargeted " << rewritten << " path(s)\n";
	return SaveMaterial(material, path, destination);
}

int CommandGraph(const std::string& path)
{
	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	std::cout << material->SaveAsStr(path.c_str()) << "\n";
	return ExitOk;
}

//! Spec envelope recognised by from-spec / save-spec. The raw SaveAsStr
//! payload is also accepted unchanged, so the wrapper is purely optional.
constexpr const char* kSpecSchemaVersion = "1";
constexpr const char* kSpecKindGraph = "graph";

bool IsFiniteJsonFloat(const picojson::value& value)
{
	return value.is<double>() && std::isfinite(value.get<double>()) &&
		value.get<double>() >= -static_cast<double>(std::numeric_limits<float>::max()) &&
		value.get<double>() <= static_cast<double>(std::numeric_limits<float>::max());
}

bool IsJsonIdentifier(const picojson::value& value)
{
	constexpr double MaxExactJsonInteger = 9007199254740991.0; // 2^53 - 1
	return value.is<double>() && std::isfinite(value.get<double>()) && value.get<double>() >= 0.0 &&
		value.get<double>() <= MaxExactJsonInteger && std::floor(value.get<double>()) == value.get<double>();
}

bool IsJsonTextureType(const picojson::value& value)
{
	return IsJsonIdentifier(value) && value.get<double>() <= static_cast<double>(EffekseerMaterial::TextureValueType::Value);
}

const picojson::value* FindJsonMember(const picojson::object& object, const char* name)
{
	const auto found = object.find(name);
	return found == object.end() ? nullptr : &found->second;
}

bool ValidateNumberMembers(const picojson::object& object, std::initializer_list<const char*> names)
{
	for (const auto* name : names)
	{
		const auto* value = FindJsonMember(object, name);
		if (value == nullptr || !IsFiniteJsonFloat(*value))
		{
			return false;
		}
	}
	return true;
}

bool ValidateDescriptions(const picojson::value& value)
{
	if (!value.is<picojson::array>())
	{
		return false;
	}
	for (const auto& description : value.get<picojson::array>())
	{
		if (!description.is<picojson::object>())
		{
			return false;
		}
		const auto& object = description.get<picojson::object>();
		const auto* summary = FindJsonMember(object, "Summary");
		const auto* detail = FindJsonMember(object, "Detail");
		if (summary == nullptr || detail == nullptr || !summary->is<std::string>() || !detail->is<std::string>())
		{
			return false;
		}
	}
	return true;
}

bool ValidateGradient(const picojson::value& value)
{
	if (!value.is<picojson::object>())
	{
		return false;
	}
	const auto& object = value.get<picojson::object>();
	const auto* colors = FindJsonMember(object, "Colors");
	const auto* alphas = FindJsonMember(object, "Alphas");
	if (colors == nullptr || alphas == nullptr || !colors->is<picojson::array>() || !alphas->is<picojson::array>() ||
		colors->get<picojson::array>().size() > Effekseer::Gradient::KeyMax ||
		alphas->get<picojson::array>().size() > Effekseer::Gradient::KeyMax)
	{
		return false;
	}
	for (const auto& color : colors->get<picojson::array>())
	{
		if (!color.is<picojson::object>() ||
			!ValidateNumberMembers(color.get<picojson::object>(), {"R", "G", "B", "Intensity", "Position"}))
		{
			return false;
		}
	}
	for (const auto& alpha : alphas->get<picojson::array>())
	{
		if (!alpha.is<picojson::object>() || !ValidateNumberMembers(alpha.get<picojson::object>(), {"Alpha", "Position"}))
		{
			return false;
		}
	}
	return true;
}

bool ValidateProperty(const picojson::value& value, EffekseerMaterial::ValueType type)
{
	if (!value.is<picojson::object>())
	{
		return false;
	}
	const auto& object = value.get<picojson::object>();
	switch (type)
	{
	case EffekseerMaterial::ValueType::Float1: return ValidateNumberMembers(object, {"Value1"});
	case EffekseerMaterial::ValueType::Float2: return ValidateNumberMembers(object, {"Value1", "Value2"});
	case EffekseerMaterial::ValueType::Float3: return ValidateNumberMembers(object, {"Value1", "Value2", "Value3"});
	case EffekseerMaterial::ValueType::Float4: return ValidateNumberMembers(object, {"Value1", "Value2", "Value3", "Value4"});
	case EffekseerMaterial::ValueType::Bool:
	{
		const auto* field = FindJsonMember(object, "Value");
		return field != nullptr && field->is<bool>();
	}
	case EffekseerMaterial::ValueType::String:
	case EffekseerMaterial::ValueType::Texture:
	{
		const auto* field = FindJsonMember(object, "Value");
		return field != nullptr && field->is<std::string>();
	}
	case EffekseerMaterial::ValueType::Int:
	case EffekseerMaterial::ValueType::Enum: return ValidateNumberMembers(object, {"Value"});
	case EffekseerMaterial::ValueType::Gradient: return ValidateGradient(value);
	default: return false;
	}
}

bool ValidateGraphObject(const picojson::value& graphValue,
						 const std::shared_ptr<EffekseerMaterial::Library>& library,
						 std::string& error)
{
	if (!graphValue.is<picojson::object>())
	{
		error = "graph must be an object";
		return false;
	}
	const auto& graph = graphValue.get<picojson::object>();
	const auto* project = FindJsonMember(graph, "Project");
	const auto* nodesValue = FindJsonMember(graph, "Nodes");
	const auto* linksValue = FindJsonMember(graph, "Links");
	const auto* customData = FindJsonMember(graph, "CustomData");
	const auto* customDataDescs = FindJsonMember(graph, "CustomDataDescs");
	const auto* textures = FindJsonMember(graph, "Textures");
	if (project == nullptr || !project->is<std::string>() || project->get<std::string>() != "EffekseerMaterial" ||
		nodesValue == nullptr || !nodesValue->is<picojson::array>() || linksValue == nullptr || !linksValue->is<picojson::array>() ||
		customData == nullptr || !customData->is<picojson::array>() || customData->get<picojson::array>().size() != 2 ||
		customDataDescs == nullptr || !customDataDescs->is<picojson::array>() || customDataDescs->get<picojson::array>().size() != 2 ||
		textures == nullptr || !textures->is<picojson::array>())
	{
		error = "graph must contain Project, Nodes, Links, two CustomData entries, two CustomDataDescs entries, and Textures";
		return false;
	}

	const auto& nodes = nodesValue->get<picojson::array>();
	const auto& links = linksValue->get<picojson::array>();
	if (nodes.empty() || nodes.size() > 4096 || links.size() > 16384)
	{
		error = "graph node or link count is out of bounds";
		return false;
	}

	struct NodeReference
	{
		uint64_t guid;
		std::shared_ptr<EffekseerMaterial::NodeParameter> parameter;
		std::shared_ptr<EffekseerMaterial::Node> validationNode;
	};
	std::vector<NodeReference> nodeReferences;
	nodeReferences.reserve(nodes.size());
	for (const auto& nodeValue : nodes)
	{
		if (!nodeValue.is<picojson::object>())
		{
			error = "each graph node must be an object";
			return false;
		}
		const auto& node = nodeValue.get<picojson::object>();
		const auto* guid = FindJsonMember(node, "GUID");
		const auto* type = FindJsonMember(node, "Type");
		const auto* posX = FindJsonMember(node, "PosX");
		const auto* posY = FindJsonMember(node, "PosY");
		const auto* properties = FindJsonMember(node, "Props");
		if (guid == nullptr || !IsJsonIdentifier(*guid) || type == nullptr || !type->is<std::string>() ||
			posX == nullptr || !IsFiniteJsonFloat(*posX) || posY == nullptr || !IsFiniteJsonFloat(*posY) ||
			properties == nullptr || !properties->is<picojson::array>())
		{
			error = "each graph node needs finite GUID/position values, Type, and Props";
			return false;
		}
		const auto parameterSource = library->FindContentWithTypeName(type->get<std::string>().c_str());
		if (parameterSource == nullptr)
		{
			error = "graph refers to an unknown node type";
			return false;
		}
		auto parameter = parameterSource->Create();
		const uint64_t id = static_cast<uint64_t>(guid->get<double>());
		if (std::any_of(nodeReferences.begin(), nodeReferences.end(), [id](const NodeReference& other) { return other.guid == id; }) ||
			properties->get<picojson::array>().size() != parameter->Properties.size())
		{
			error = "graph has duplicate GUIDs or a node with an invalid property count";
			return false;
		}
		for (size_t index = 0; index < parameter->Properties.size(); index++)
		{
			if (!ValidateProperty(properties->get<picojson::array>()[index], parameter->Properties[index]->Type))
			{
				error = "graph has a property with an invalid type or value";
				return false;
			}
		}
		const auto* descriptions = FindJsonMember(node, "Descs");
		if (descriptions != nullptr && !ValidateDescriptions(*descriptions))
		{
			error = "graph has invalid node descriptions";
			return false;
		}
		nodeReferences.push_back({id, parameter, nullptr});
	}

	// Material::LoadFromStr does not report failed connections. Build the same
	// node types in an isolated graph first, so a supplied spec cannot silently
	// lose an invalid, duplicate, or cyclic link during import.
	auto linkMaterial = std::make_shared<EffekseerMaterial::Material>();
	linkMaterial->Initialize();
	for (auto& reference : nodeReferences)
	{
		reference.validationNode = linkMaterial->CreateNode(reference.parameter, false);
	}

	for (const auto& data : customData->get<picojson::array>())
	{
		if (!data.is<picojson::object>() || !ValidateNumberMembers(data.get<picojson::object>(), {"Value1", "Value2", "Value3", "Value4"}))
		{
			error = "graph has invalid custom-data defaults";
			return false;
		}
	}
	for (const auto& descriptions : customDataDescs->get<picojson::array>())
	{
		if (!ValidateDescriptions(descriptions))
		{
			error = "graph has invalid custom-data descriptions";
			return false;
		}
	}
	for (const auto& texture : textures->get<picojson::array>())
	{
		if (!texture.is<picojson::object>())
		{
			error = "graph has an invalid texture entry";
			return false;
		}
		const auto& object = texture.get<picojson::object>();
		const auto* path = FindJsonMember(object, "Path");
		const auto* type = FindJsonMember(object, "Type");
		if (path == nullptr || !path->is<std::string>() || type == nullptr || !IsJsonTextureType(*type))
		{
			error = "graph has an invalid texture entry";
			return false;
		}
	}

	std::vector<uint64_t> linkGuids;
	std::vector<std::pair<uint64_t, std::string>> connectedInputs;
	for (const auto& linkValue : links)
	{
		if (!linkValue.is<picojson::object>())
		{
			error = "each graph link must be an object";
			return false;
		}
		const auto& link = linkValue.get<picojson::object>();
		const auto* guid = FindJsonMember(link, "GUID");
		const auto* input = FindJsonMember(link, "InputGUID");
		const auto* output = FindJsonMember(link, "OutputGUID");
		const auto* inputPin = FindJsonMember(link, "InputPin");
		const auto* outputPin = FindJsonMember(link, "OutputPin");
		if (guid == nullptr || !IsJsonIdentifier(*guid) || input == nullptr || !IsJsonIdentifier(*input) ||
			output == nullptr || !IsJsonIdentifier(*output) || inputPin == nullptr || !inputPin->is<std::string>() ||
			outputPin == nullptr || !outputPin->is<std::string>())
		{
			error = "graph has an invalid link";
			return false;
		}

		const uint64_t linkId = static_cast<uint64_t>(guid->get<double>());
		const uint64_t inputId = static_cast<uint64_t>(input->get<double>());
		const uint64_t outputId = static_cast<uint64_t>(output->get<double>());
		const auto findNode = [&nodeReferences](uint64_t id) {
			return std::find_if(nodeReferences.begin(), nodeReferences.end(),
				[id](const NodeReference& node) { return node.guid == id; });
		};
		const auto inputNode = findNode(inputId);
		const auto outputNode = findNode(outputId);
		if (inputNode == nodeReferences.end() || outputNode == nodeReferences.end() ||
			std::find(linkGuids.begin(), linkGuids.end(), linkId) != linkGuids.end())
		{
			error = "graph link refers to a missing node or duplicates a GUID";
			return false;
		}

		std::string outputPinName = outputPin->get<std::string>();
		if (outputNode->parameter->TypeName == "SampleTexture" && outputPinName == "Output")
		{
			outputPinName = "RGBA";
		}
		const int32_t inputPinIndex = inputNode->validationNode->GetInputPinIndex(inputPin->get<std::string>());
		const int32_t outputPinIndex = outputNode->validationNode->GetOutputPinIndex(outputPinName);
		const auto inputKey = std::make_pair(inputId, inputPin->get<std::string>());
		if (inputPinIndex < 0 || outputPinIndex < 0 ||
			std::find(connectedInputs.begin(), connectedInputs.end(), inputKey) != connectedInputs.end() ||
			linkMaterial->ConnectPin(inputNode->validationNode->InputPins[static_cast<size_t>(inputPinIndex)],
				outputNode->validationNode->OutputPins[static_cast<size_t>(outputPinIndex)]) != EffekseerMaterial::ConnectResultType::OK)
		{
			error = "graph has an invalid, conflicting, or cyclic link";
			return false;
		}
		linkGuids.push_back(linkId);
		connectedInputs.push_back(std::move(inputKey));
	}
	return true;
}

bool ParseGraphSpec(const std::string& body,
					const std::shared_ptr<EffekseerMaterial::Library>& library,
					std::string& graph,
					std::string& error)
{
	picojson::value root;
	const std::string parseError = picojson::parse(root, body);
	if (!parseError.empty() || !root.is<picojson::object>())
	{
		error = parseError.empty() ? "spec must be a JSON object" : parseError;
		return false;
	}

	const auto& object = root.get<picojson::object>();
	const auto schema = object.find("schema_version");
	const auto kind = object.find("kind");
	const auto wrappedGraph = object.find("graph");
	const bool isEnvelope = schema != object.end() || kind != object.end() || wrappedGraph != object.end();
	const picojson::value* graphValue = &root;
	if (isEnvelope)
	{
		if (schema == object.end() || kind == object.end() || wrappedGraph == object.end() ||
			!schema->second.is<std::string>() || schema->second.get<std::string>() != kSpecSchemaVersion ||
			!kind->second.is<std::string>() || kind->second.get<std::string>() != kSpecKindGraph ||
			!wrappedGraph->second.is<picojson::object>())
		{
			error = "spec must contain schema_version=\"1\", kind=\"graph\", and an object graph";
			return false;
		}
		graphValue = &wrappedGraph->second;
	}

	if (!ValidateGraphObject(*graphValue, library, error))
	{
		return false;
	}
	graph = graphValue->serialize();
	return true;
}

int CommandSaveSpec(const std::string& path, const std::string& destination)
{
	if (destination.empty())
	{
		std::cerr << "efkmatc: save-spec needs -o OUTSPEC.json\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	// Serialize texture paths relative to the spec itself. from-spec resolves
	// them against specPath, then Material::Save re-relativizes them to OUTPUT.
	const auto raw = material->SaveAsStr(destination.c_str());
	if (raw.empty())
	{
		std::cerr << "efkmatc: empty graph for " << path << "\n";
		return ExitLoadFailure;
	}

	const auto parent = std::filesystem::path(destination).parent_path();
	if (!parent.empty())
	{
		std::error_code ec;
		std::filesystem::create_directories(parent, ec);
	}

	std::ofstream out(destination, std::ios::binary | std::ios::trunc);
	if (!out)
	{
		std::cerr << "efkmatc: cannot write " << destination << "\n";
		return ExitEditFailure;
	}
	out << "{\"schema_version\":\"" << kSpecSchemaVersion << "\",\"kind\":\"" << kSpecKindGraph
		<< "\",\"graph\":" << raw << "}\n";
	out.close();
	if (!out)
	{
		std::cerr << "efkmatc: failed while writing " << destination << "\n";
		return ExitEditFailure;
	}

	std::error_code sizeError;
	const auto written = std::filesystem::file_size(destination, sizeError);
	if (sizeError)
	{
		std::cerr << "efkmatc: cannot stat " << destination << ": " << sizeError.message() << "\n";
		return ExitEditFailure;
	}
	std::cout << "wrote " << destination << " (" << written << " bytes)\n";
	return ExitOk;
}

int CommandFromSpec(const std::string& specPath, const std::string& destination)
{
	if (destination.empty())
	{
		std::cerr << "efkmatc: from-spec needs -o OUT.efkmat\n";
		return ExitUsage;
	}

	std::string body;
	if (!ReadTextFile(specPath, body))
	{
		std::cerr << "efkmatc: cannot read " << specPath << "\n";
		return ExitLoadFailure;
	}

	auto library = std::make_shared<EffekseerMaterial::Library>();
	std::string graph;
	std::string parseError;
	if (!ParseGraphSpec(body, library, graph, parseError))
	{
		std::cerr << "efkmatc: invalid graph spec " << specPath << ": " << parseError << "\n";
		return ExitLoadFailure;
	}

	auto material = std::make_shared<EffekseerMaterial::Material>();
	// Initialize supplies the two CustomData descriptions that Material::Save
	// requires before LoadFromStr replaces the graph contents.
	material->Initialize();
	// Graph texture paths are relative to the spec, not the output material.
	material->LoadFromStr(graph.c_str(), library, specPath.c_str());

	if (FindOutputNode(material) == nullptr)
	{
		std::cerr << "efkmatc: spec did not produce an Output node: " << specPath << "\n";
		return ExitEditFailure;
	}

	return SaveMaterial(material, specPath, destination);
}

//! Find a node in the library by TypeName (e.g. "TextureObjectParameter"). The
//! library self-seeds all known node types; consumers should not instantiate
//! NodeParameter subclasses by hand.
std::shared_ptr<EffekseerMaterial::NodeParameter> FindLibraryNode(const std::shared_ptr<EffekseerMaterial::Library>& library,
																   const std::string& typeName)
{
	auto content = library->FindContentWithTypeName(typeName.c_str());
	if (content == nullptr)
	{
		return nullptr;
	}
	return content->Create();
}

int CommandAddTexture(const std::string& path, const std::string& destination, const std::string& name, const std::string& texturePath)
{
	if (name.empty() || texturePath.empty())
	{
		std::cerr << "efkmatc: add-texture needs --name NAME and --path PATH\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto library = std::make_shared<EffekseerMaterial::Library>();
	auto parameter = FindLibraryNode(library, "TextureObjectParameter");
	if (parameter == nullptr)
	{
		std::cerr << "efkmatc: library does not expose TextureObjectParameter\n";
		return ExitEditFailure;
	}

	auto node = material->CreateNode(parameter, true);
	node->Pos = EffekseerMaterial::Vector2DF(0.0f, 200.0f);

	const int32_t nameIndex = node->Parameter->GetPropertyIndex("Name");
	const int32_t textureIndex = node->Parameter->GetPropertyIndex("Texture");
	if (nameIndex < 0 || textureIndex < 0 || static_cast<size_t>(nameIndex) >= node->Properties.size() ||
		static_cast<size_t>(textureIndex) >= node->Properties.size())
	{
		std::cerr << "efkmatc: TextureObjectParameter has an unexpected property layout\n";
		return ExitEditFailure;
	}
	material->ChangeValue(node->Properties[static_cast<size_t>(nameIndex)], name);
	const auto resolved = ResolvePath(texturePath, BaseDirectory(destination));
	if (!resolved.empty() && !std::filesystem::exists(resolved))
	{
		std::cerr << "efkmatc: warning: texture does not exist: " << resolved << "\n";
	}
	material->ChangeValue(node->Properties[textureIndex], resolved);

	std::cout << "added TextureObjectParameter " << name << " node=" << node->GUID << "\n";
	return SaveMaterial(material, path, destination);
}

int CommandAddUniform(const std::string& path, const std::string& destination, const std::string& name, const std::string& defaultValue)
{
	if (name.empty() || defaultValue.empty())
	{
		std::cerr << "efkmatc: add-uniform needs --name NAME and --default x[,y,z,w]\n";
		return ExitUsage;
	}

	std::array<float, 4> parsed{};
	int components = 0;
	if (!ParseFloat4(defaultValue, parsed, &components))
	{
		std::cerr << "efkmatc: --default needs one through four finite comma-separated values\n";
		return ExitUsage;
	}

	// Pick the matching ParameterN based on the validated component count.
	const std::array<std::string, 4> typeNames = {"Parameter1", "Parameter2", "Parameter3", "Parameter4"};
	const std::string typeName = typeNames[static_cast<size_t>(components - 1)];

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto library = std::make_shared<EffekseerMaterial::Library>();
	auto parameter = FindLibraryNode(library, typeName);
	if (parameter == nullptr)
	{
		std::cerr << "efkmatc: library does not expose " << typeName << "\n";
		return ExitEditFailure;
	}

	auto node = material->CreateNode(parameter, true);
	node->Pos = EffekseerMaterial::Vector2DF(0.0f, 300.0f);

	const int32_t nameIndex = node->Parameter->GetPropertyIndex("Name");
	const int32_t valueIndex = node->Parameter->GetPropertyIndex("Value");
	if (nameIndex < 0 || valueIndex < 0 || static_cast<size_t>(nameIndex) >= node->Properties.size() ||
		static_cast<size_t>(valueIndex) >= node->Properties.size())
	{
		std::cerr << "efkmatc: " << typeName << " has an unexpected property layout\n";
		return ExitEditFailure;
	}
	material->ChangeValue(node->Properties[static_cast<size_t>(nameIndex)], name);
	material->ChangeValue(node->Properties[static_cast<size_t>(valueIndex)], parsed);

	std::cout << "added " << typeName << " " << name << " node=" << node->GUID << "\n";
	return SaveMaterial(material, path, destination);
}

int CommandSetCustomData(const std::string& path, const std::string& destination, int slot, const std::string& defaultValue)
{
	if (slot < 0 || slot > 1)
	{
		std::cerr << "efkmatc: set-custom-data --slot must be 0 or 1\n";
		return ExitUsage;
	}
	if (defaultValue.empty())
	{
		std::cerr << "efkmatc: set-custom-data needs --default x[,y,z,w]\n";
		return ExitUsage;
	}

	std::array<float, 4> parsed{};
	if (!ParseFloat4(defaultValue, parsed))
	{
		std::cerr << "efkmatc: cannot parse --default " << defaultValue << "\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	material->CustomData[slot].Values = parsed;
	std::cout << "CustomData[" << slot << "] = " << Floats4(parsed) << "\n";
	return SaveMaterial(material, path, destination);
}

int CommandSetShadingModel(const std::string& path, const std::string& destination, const std::string& value)
{
	int shadingModel = -1;
	if (value == "lit" || value == "Lit")
	{
		shadingModel = 0;
	}
	else if (value == "unlit" || value == "Unlit")
	{
		shadingModel = 1;
	}
	if (shadingModel < 0)
	{
		std::cerr << "efkmatc: set-shading-model --shading-model must be lit or unlit\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto output = FindOutputNode(material);
	if (output == nullptr)
	{
		std::cerr << "efkmatc: no Output node in " << path << "\n";
		return ExitEditFailure;
	}

	const int32_t index = output->Parameter->GetPropertyIndex("ShadingModel");
	if (index < 0 || static_cast<size_t>(index) >= output->Properties.size())
	{
		std::cerr << "efkmatc: Output node has an unexpected ShadingModel property layout\n";
		return ExitEditFailure;
	}
	std::array<float, 4> valueArray{};
	valueArray[0] = static_cast<float>(shadingModel);
	material->ChangeValue(output->Properties[index], valueArray);

	std::cout << "ShadingModel = " << value << "\n";
	return SaveMaterial(material, path, destination);
}

int CommandValidate(const std::string& path)
{
	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	int warnings = 0;
	for (const auto& node : material->GetNodes())
	{
		const auto warning = node->Parameter->GetWarning(material, node);
		if (warning != EffekseerMaterial::WarningType::None)
		{
			warnings++;
			std::cout << "warning " << WarningName(warning) << " node=" << node->GUID
					  << " type=" << node->Parameter->TypeName << "\n";
		}
	}

	std::cout << "nodes " << material->GetNodes().size() << ", warnings " << warnings << "\n";
	return warnings == 0 ? ExitOk : ExitWarnings;
}

int CommandExport(const std::string& path, const std::string& outDirectory)
{
	if (outDirectory.empty())
	{
		return Usage();
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	auto outputNode = FindOutputNode(material);
	if (outputNode == nullptr)
	{
		std::cerr << "efkmatc: no output node in " << path << "\n";
		return ExitLoadFailure;
	}

	EffekseerMaterial::TextExporter exporter;
	const auto result = exporter.Export(material, outputNode);

	std::error_code ec;
	std::filesystem::create_directories(outDirectory, ec);

	const auto stem = std::filesystem::path(path).stem().string();
	const auto target = std::filesystem::path(outDirectory) / (stem + ".generic.txt");

	std::ofstream out(target, std::ios::binary);
	if (!out)
	{
		std::cerr << "efkmatc: cannot write " << target.string() << "\n";
		return ExitCompileFailure;
	}
	out << result.Code;
	out.close();

	std::cout << "wrote " << target.string() << " (" << result.Code.size() << " bytes)\n";
	std::cout << "shadingModel " << result.ShadingModel << ", hasRefraction "
			  << (result.HasRefraction ? "yes" : "no") << ", customData " << result.CustomData1 << "/"
			  << result.CustomData2 << ", uniforms " << result.Uniforms.size() << ", textures "
			  << result.Textures.size() << "\n";
	return ExitOk;
}

//! Report which platforms a produced cache actually contains. Nothing is
//! claimed for platforms whose compiler library was not present.
int ReportCompiled(const std::string& path, const std::string& sourcePath)
{
	std::vector<uint8_t> compiledData;
	std::vector<uint8_t> sourceData;
	if (!ReadFile(path, compiledData) || !ReadFile(sourcePath, sourceData))
	{
		std::cerr << "efkmatc: cannot re-read outputs for verification\n";
		return ExitCompileFailure;
	}

	Effekseer::MaterialFile source;
	if (!source.Load(sourceData.data(), static_cast<int32_t>(sourceData.size())))
	{
		std::cerr << "efkmatc: cannot re-read source material for verification\n";
		return ExitCompileFailure;
	}

	Effekseer::CompiledMaterial compiled;
	if (!compiled.Load(compiledData.data(), static_cast<int32_t>(compiledData.size())))
	{
		std::cerr << "efkmatc: produced cache is not loadable: " << path << "\n";
		return ExitCompileFailure;
	}

	// CompiledMaterial::Load reads the GUID into a local and never assigns the
	// GUID member, so that member is always 0 here. Read it straight out of the
	// header instead: "eMCB" magic, int32 version, then the uint64 GUID.
	constexpr size_t GuidOffset = 4 + sizeof(int32_t);
	if (compiledData.size() < GuidOffset + sizeof(uint64_t))
	{
		std::cerr << "efkmatc: produced cache is too small to hold a header: " << path << "\n";
		return ExitCompileFailure;
	}

	uint64_t storedGuid = 0;
	memcpy(&storedGuid, compiledData.data() + GuidOffset, sizeof(storedGuid));

	if (storedGuid != source.GetGUID())
	{
		std::cerr << "efkmatc: GUID mismatch (cache " << storedGuid << " vs source " << source.GetGUID()
				  << ")\n";
		return ExitCompileFailure;
	}

	const std::pair<Effekseer::CompiledMaterialPlatformType, const char*> platforms[] = {
		{Effekseer::CompiledMaterialPlatformType::OpenGL, "OpenGL"},
		{Effekseer::CompiledMaterialPlatformType::Metal, "Metal"},
		{Effekseer::CompiledMaterialPlatformType::Vulkan, "Vulkan"},
		{Effekseer::CompiledMaterialPlatformType::WebGPU, "WebGPU"},
		{Effekseer::CompiledMaterialPlatformType::DirectX9, "DirectX9"},
		{Effekseer::CompiledMaterialPlatformType::DirectX11, "DirectX11"},
		{Effekseer::CompiledMaterialPlatformType::DirectX12, "DirectX12"},
	};

	std::string present;
	for (const auto& platform : platforms)
	{
		if (compiled.GetHasValue(platform.first))
		{
			present += present.empty() ? "" : ",";
			present += platform.second;
		}
	}

	std::cout << "verified guid=" << storedGuid << " platforms=" << (present.empty() ? "(none)" : present)
			  << "\n";
	return present.empty() ? ExitCompileFailure : ExitOk;
}

int CompileOne(CompiledMaterialGenerator& generator,
			   const std::string& source,
			   const std::string& destination,
			   bool verify)
{
	// The generator writes with a plain ofstream and does not create missing
	// directories, so make the destination directory first.
	const auto parent = std::filesystem::path(destination).parent_path();
	if (!parent.empty())
	{
		std::error_code ec;
		std::filesystem::create_directories(parent, ec);
	}

	if (!generator.Compile(destination.c_str(), source.c_str()))
	{
		std::cerr << "efkmatc: compile failed for " << source << "\n";
		return ExitCompileFailure;
	}

	// The generator reports success even when the output stream could not be
	// opened, so confirm a non-empty file actually landed.
	std::error_code sizeError;
	const auto written = std::filesystem::file_size(destination, sizeError);
	if (sizeError || written == 0)
	{
		std::cerr << "efkmatc: no cache was written to " << destination << "\n";
		return ExitCompileFailure;
	}

	std::cout << "compiled " << source << " -> " << destination << " (" << written << " bytes)\n";
	return verify ? ReportCompiled(destination, source) : ExitOk;
}

int CommandCompile(const std::string& path,
				   const std::string& output,
				   const std::string& toolsDirectory,
				   const std::string& batchDirectory,
				   bool verify)
{
	CompiledMaterialGenerator generator;
	const auto tools = toolsDirectory.empty() ? std::string(".") : toolsDirectory;
	generator.Initialize(tools.c_str());

	if (!batchDirectory.empty())
	{
		// Walk with explicit error codes: the throwing overloads would abort the
		// whole run on a single unreadable subdirectory.
		std::vector<std::string> inputs;
		std::error_code ec;
		auto entry = std::filesystem::recursive_directory_iterator(batchDirectory, ec);
		if (ec)
		{
			std::cerr << "efkmatc: cannot walk " << batchDirectory << ": " << ec.message() << "\n";
			return ExitLoadFailure;
		}

		const std::filesystem::recursive_directory_iterator end;
		for (; entry != end; entry.increment(ec))
		{
			if (ec)
			{
				std::cerr << "efkmatc: cannot walk " << batchDirectory << ": " << ec.message() << "\n";
				return ExitLoadFailure;
			}

			std::error_code kindError;
			if (entry->is_regular_file(kindError) && entry->path().extension() == ".efkmat")
			{
				inputs.push_back(entry->path().string());
			}
		}
		std::sort(inputs.begin(), inputs.end());

		int failures = 0;
		for (const auto& input : inputs)
		{
			if (CompileOne(generator, input, input + "d", verify) != ExitOk)
			{
				failures++;
			}
		}
		std::cout << "batch " << inputs.size() << " material(s), " << failures << " failure(s)\n";
		return failures == 0 ? ExitOk : ExitCompileFailure;
	}

	if (path.empty())
	{
		return Usage();
	}
	return CompileOne(generator, path, output.empty() ? path + "d" : output, verify);
}

} // namespace

//! Renders an offscreen preview. Kept behind a compile guard because it needs
//! EGL's surfaceless platform; hosts without it still get every other command,
//! and say so plainly rather than failing in an obscure way.
int CommandRender(const std::string& path,
				  const std::string& output,
				  const std::string& modelName,
				  const std::string& resourceDirectory,
				  float time)
{
#if !EFKMATC_ENABLE_RENDER
	(void)path;
	(void)output;
	(void)modelName;
	(void)resourceDirectory;
	(void)time;
	std::cerr << "efkmatc: this build has no offscreen renderer; it requires EGL\n";
	return ExitRenderFailure;
#else
	if (output.empty())
	{
		std::cerr << "efkmatc: render needs an output file (-o OUT.png)\n";
		return ExitUsage;
	}

	efkmatc::RenderRequest request;
	request.outputPath = output;
	request.time = time;
	request.resourceDirectory = resourceDirectory;

	if (modelName.empty() || modelName == "screen")
	{
		request.model = efkmatc::PreviewModel::Screen;
	}
	else if (modelName == "sphere")
	{
		request.model = efkmatc::PreviewModel::Sphere;
		if (resourceDirectory.empty())
		{
			std::cerr << "efkmatc: --model sphere needs --resources DIR containing "
						 "resources/meshes/sphere.obj\n";
			return ExitUsage;
		}
	}
	else
	{
		std::cerr << "efkmatc: unknown --model " << modelName << " (screen or sphere)\n";
		return ExitUsage;
	}

	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	std::string error;
	if (!efkmatc::RenderMaterial(material, request, error))
	{
		std::cerr << "efkmatc: " << error << "\n";
		return ExitRenderFailure;
	}

	std::cout << "rendered " << path << " -> " << output << " (" << efkmatc::LastDeviceDescription()
			  << ")\n";
	return ExitOk;
#endif
}

int main(int argc, char** argv)
{
	if (argc < 2)
	{
		return Usage();
	}

	const std::string command = argv[1];
	std::string input;
	std::string output;
	std::string toolsDirectory;
	std::string batchDirectory;
	std::string modelName;
	std::string resourceDirectory;
	float timeValue = 0.0f;
	std::string propertyName;
	std::string parameterName;
	std::string stringValue;
	std::string floatValue;
	std::string fromPrefix;
	std::string toPrefix;
	std::string newName;
	std::string defaultValue;
	std::string shadingModelValue;
	int customDataSlot = -1;
	uint64_t nodeGuid = 0;
	bool asJson = false;
	bool verify = false;
	bool inPlace = false;
	bool sawToPrefix = false;

	for (int i = 2; i < argc; i++)
	{
		const std::string argument = argv[i];
		const bool hasValue = (i + 1) < argc;
		if (argument == "--json")
		{
			asJson = true;
		}
		else if (argument == "--verify")
		{
			verify = true;
		}
		else if (argument == "--in-place")
		{
			inPlace = true;
		}
		else if (argument == "--prop" && hasValue)
		{
			propertyName = argv[++i];
		}
		else if (argument == "--param" && hasValue)
		{
			parameterName = argv[++i];
		}
		else if ((argument == "--str" || argument == "--path") && hasValue)
		{
			stringValue = argv[++i];
		}
		else if (argument == "--value" && hasValue)
		{
			floatValue = argv[++i];
		}
		else if (argument == "--from" && hasValue)
		{
			fromPrefix = argv[++i];
		}
		else if (argument == "--to" && hasValue)
		{
			toPrefix = argv[++i];
			sawToPrefix = true;
		}
		else if (argument == "--node" && hasValue)
		{
			try
			{
				nodeGuid = std::stoull(argv[++i]);
			}
			catch (const std::exception&)
			{
				std::cerr << "efkmatc: --node expects a numeric GUID\n";
				return ExitUsage;
			}
		}
		else if (argument == "-o" && hasValue)
		{
			output = argv[++i];
		}
		else if (argument == "--tools-dir" && hasValue)
		{
			toolsDirectory = argv[++i];
		}
		else if (argument == "--model" && hasValue)
		{
			modelName = argv[++i];
		}
		else if (argument == "--resources" && hasValue)
		{
			resourceDirectory = argv[++i];
		}
		else if (argument == "--time" && hasValue)
		{
			try
			{
				timeValue = std::stof(argv[++i]);
			}
			catch (const std::exception&)
			{
				std::cerr << "efkmatc: --time expects a number\n";
				return ExitUsage;
			}
		}
		else if (argument == "--batch" && hasValue)
		{
			batchDirectory = argv[++i];
		}
		else if (argument == "--name" && hasValue)
		{
			newName = argv[++i];
		}
		else if (argument == "--default" && hasValue)
		{
			defaultValue = argv[++i];
		}
		else if (argument == "--slot" && hasValue)
		{
			try
			{
				customDataSlot = std::stoi(argv[++i]);
			}
			catch (const std::exception&)
			{
				std::cerr << "efkmatc: --slot expects an integer\n";
				return ExitUsage;
			}
		}
		else if (argument == "--shading-model" && hasValue)
		{
			shadingModelValue = argv[++i];
		}
		else if (!argument.empty() && argument[0] == '-')
		{
			std::cerr << "efkmatc: unknown option " << argument << "\n";
			return Usage();
		}
		else if (input.empty())
		{
			input = argument;
		}
		else
		{
			std::cerr << "efkmatc: unexpected argument " << argument << "\n";
			return Usage();
		}
	}

	if (command == "compile")
	{
		return CommandCompile(input, output, toolsDirectory, batchDirectory, verify);
	}

	if (command == "render")
	{
		if (input.empty())
		{
			return Usage();
		}
		return CommandRender(input, output, modelName, resourceDirectory, timeValue);
	}

	if (input.empty())
	{
		return Usage();
	}

	if (command == "info")
	{
		return CommandInfo(input, asJson);
	}
	if (command == "graph")
	{
		return CommandGraph(input);
	}
	if (command == "validate")
	{
		return CommandValidate(input);
	}
	if (command == "export")
	{
		return CommandExport(input, output);
	}
	if (command == "props")
	{
		return CommandProps(input);
	}

	if (command == "set" || command == "set-texture" || command == "set-value" || command == "retarget" ||
		command == "add-texture" || command == "add-uniform" || command == "set-custom-data" ||
		command == "set-shading-model")
	{
		std::string destination;
		if (!ResolveEditTarget(input, output, inPlace, destination))
		{
			return ExitUsage;
		}

		if (command == "set")
		{
			return CommandSet(input, destination, nodeGuid, propertyName, stringValue, floatValue);
		}
		if (command == "set-texture")
		{
			return CommandSetNamed(input, destination, parameterName, true, stringValue, floatValue);
		}
		if (command == "set-value")
		{
			return CommandSetNamed(input, destination, parameterName, false, stringValue, floatValue);
		}
		if (command == "retarget")
		{
			if (!sawToPrefix)
			{
				std::cerr << "efkmatc: retarget needs --to PREFIX (use --to '' to strip)\n";
				return ExitUsage;
			}
			return CommandRetarget(input, destination, fromPrefix, toPrefix);
		}
		if (command == "add-texture")
		{
			return CommandAddTexture(input, destination, newName, stringValue);
		}
		if (command == "add-uniform")
		{
			return CommandAddUniform(input, destination, newName, defaultValue);
		}
		if (command == "set-custom-data")
		{
			return CommandSetCustomData(input, destination, customDataSlot, defaultValue);
		}
		if (command == "set-shading-model")
		{
			return CommandSetShadingModel(input, destination, shadingModelValue);
		}
	}

	if (command == "from-spec")
	{
		return CommandFromSpec(input, output);
	}
	if (command == "save-spec")
	{
		return CommandSaveSpec(input, output);
	}

	std::cerr << "efkmatc: unknown command " << command << "\n";
	return Usage();
}
