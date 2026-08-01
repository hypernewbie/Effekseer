// [UAA] - START - efkmatc: offscreen material preview rendering
#pragma once

#include <memory>
#include <string>

namespace EffekseerMaterial
{
class Material;
}

namespace efkmatc
{

//! Which preview body the material is applied to, mirroring the editor's
//! PreviewModelType so the CLI can render the same shapes the editor shows.
enum class PreviewModel
{
	Screen,
	Sphere,
};

struct RenderRequest
{
	std::string outputPath;
	PreviewModel model = PreviewModel::Screen;
	float time = 0.0f;
	//! Directory containing "resources/meshes/*.obj"; only needed for Sphere.
	std::string resourceDirectory;
};

//! Renders a material preview without opening a window and writes a PNG.
//! Returns false and fills 'error' on failure.
bool RenderMaterial(const std::shared_ptr<EffekseerMaterial::Material>& material,
					const RenderRequest& request,
					std::string& error);

//! Human-readable description of the offscreen GL device, valid after a
//! successful RenderMaterial call. Empty if rendering never got that far.
const std::string& LastDeviceDescription();

} // namespace efkmatc
// [UAA] - END
