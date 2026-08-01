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
// Editing rewrites the graph, regenerates the shader code, and recomputes the
// GUID, which invalidates any previously compiled .efkmatd cache.
// Exit codes: 0 ok, 1 usage, 2 load failure, 3 validation warnings,
// 4 compile failure, 5 edit failure.
// [UAA] - END

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
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

bool ParseFloat4(const std::string& text, std::array<float, 4>& out)
{
	out.fill(0.0f);
	size_t index = 0;
	size_t start = 0;
	while (start <= text.size() && index < out.size())
	{
		const size_t comma = text.find(',', start);
		const std::string field = text.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
		if (field.empty())
		{
			return false;
		}
		try
		{
			out[index++] = std::stof(field);
		}
		catch (const std::exception&)
		{
			return false;
		}
		if (comma == std::string::npos)
		{
			return true;
		}
		start = comma + 1;
	}
	return index > 0;
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

	if (command == "set" || command == "set-texture" || command == "set-value" || command == "retarget")
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
		if (!sawToPrefix)
		{
			std::cerr << "efkmatc: retarget needs --to PREFIX (use --to '' to strip)\n";
			return ExitUsage;
		}
		return CommandRetarget(input, destination, fromPrefix, toPrefix);
	}

	std::cerr << "efkmatc: unknown command " << command << "\n";
	return Usage();
}
