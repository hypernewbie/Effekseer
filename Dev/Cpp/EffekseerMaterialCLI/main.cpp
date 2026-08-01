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
// Exit codes: 0 ok, 1 usage, 2 load failure, 3 validation warnings,
// 4 compile failure.
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

int Usage()
{
	std::cerr << "usage: efkmatc <command> [options]\n\n"
			  << "  info     FILE.efkmat [--json]\n"
			  << "  graph    FILE.efkmat\n"
			  << "  validate FILE.efkmat\n"
			  << "  export   FILE.efkmat -o OUTDIR\n"
			  << "  compile  FILE.efkmat [-o OUT.efkmatd] [--tools-dir DIR] [--verify]\n"
			  << "  compile  --batch DIR [--tools-dir DIR] [--verify]\n";
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

std::string BaseDirectory(const std::string& path)
{
	return std::filesystem::path(path).parent_path().string();
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

	const auto base = BaseDirectory(path);
	const auto error = material->Load(data, library, base.c_str());
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

int CommandGraph(const std::string& path)
{
	auto material = LoadGraph(path);
	if (material == nullptr)
	{
		return ExitLoadFailure;
	}

	const auto base = BaseDirectory(path);
	std::cout << material->SaveAsStr(base.c_str()) << "\n";
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
	bool asJson = false;
	bool verify = false;

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
		else if (argument == "-o" && hasValue)
		{
			output = argv[++i];
		}
		else if (argument == "--tools-dir" && hasValue)
		{
			toolsDirectory = argv[++i];
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

	std::cerr << "efkmatc: unknown command " << command << "\n";
	return Usage();
}
