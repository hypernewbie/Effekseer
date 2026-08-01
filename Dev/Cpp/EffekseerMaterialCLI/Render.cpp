// [UAA] - START - efkmatc: offscreen material preview rendering
#include "Render.h"

#define GL_GLEXT_PROTOTYPES 1
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GL/gl.h>
#include <GL/glext.h>

#include <algorithm>
#include <array>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <sstream>
#include <vector>

#include <IO/IO.h>
#include <spdlog/spdlog.h>

#include "../EffekseerMaterial/efkMat.Models.h"
#include "../EffekseerMaterial/efkMat.Parameters.h"
#include "../EffekseerMaterial/efkMat.TextExporter.h"
#include "../EffekseerMaterialEditor/Graphics/efkMat.Graphics.h"

#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "../3rdParty/stb/stb_image_write.h"

namespace efkmatc
{

namespace
{

std::string deviceDescription;

//! Offscreen GL context via EGL's surfaceless platform.
//!
//! This deliberately does NOT go through GLFW/X11. A window is a presentation
//! concern; efkmatc never presents anything, and must work where no display
//! server exists at all. Creating a hidden window would still require a live
//! display connection and would drag a GUI stack into a batch tool for nothing.
class HeadlessGL
{
public:
	~HeadlessGL()
	{
		Shutdown();
	}

	bool Initialize(std::string& error)
	{
		PreferSoftwareRasterizer();

		// The surfaceless platform is the whole point: a GL context with no
		// window and no drawable.
		auto getPlatformDisplay =
			reinterpret_cast<PFNEGLGETPLATFORMDISPLAYEXTPROC>(eglGetProcAddress("eglGetPlatformDisplayEXT"));
		if (getPlatformDisplay != nullptr)
		{
			display_ = getPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, nullptr);
		}
		if (display_ == EGL_NO_DISPLAY)
		{
			display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
		}
		if (display_ == EGL_NO_DISPLAY)
		{
			error = "no EGL display; a GL driver with EGL support is required for rendering";
			return false;
		}

		EGLint major = 0;
		EGLint minor = 0;
		if (eglInitialize(display_, &major, &minor) != EGL_TRUE)
		{
			display_ = EGL_NO_DISPLAY;
			error = "eglInitialize failed; no usable offscreen GL driver";
			return false;
		}

		const EGLint configAttributes[] = {
			EGL_SURFACE_TYPE,
			EGL_PBUFFER_BIT,
			EGL_RENDERABLE_TYPE,
			EGL_OPENGL_BIT,
			EGL_RED_SIZE,
			8,
			EGL_GREEN_SIZE,
			8,
			EGL_BLUE_SIZE,
			8,
			EGL_ALPHA_SIZE,
			8,
			EGL_DEPTH_SIZE,
			24,
			EGL_NONE,
		};

		EGLConfig config = nullptr;
		EGLint configCount = 0;
		if (eglChooseConfig(display_, configAttributes, &config, 1, &configCount) != EGL_TRUE || configCount < 1)
		{
			error = "no EGL config supporting desktop OpenGL";
			return false;
		}

		if (eglBindAPI(EGL_OPENGL_API) != EGL_TRUE)
		{
			error = "the EGL driver does not expose desktop OpenGL";
			return false;
		}

		context_ = eglCreateContext(display_, config, EGL_NO_CONTEXT, nullptr);
		if (context_ == EGL_NO_CONTEXT)
		{
			error = "eglCreateContext failed";
			return false;
		}

		if (eglMakeCurrent(display_, EGL_NO_SURFACE, EGL_NO_SURFACE, context_) != EGL_TRUE)
		{
			error = "eglMakeCurrent failed; EGL_KHR_surfaceless_context is required";
			return false;
		}

		const auto* renderer = glGetString(GL_RENDERER);
		const auto* version = glGetString(GL_VERSION);
		deviceDescription = std::string(renderer != nullptr ? reinterpret_cast<const char*>(renderer) : "unknown") +
							" / GL " + (version != nullptr ? reinterpret_cast<const char*>(version) : "unknown");
		return true;
	}

private:
	//! Default to the software rasterizer.
	//!
	//! Two reasons, both learned the hard way. A previewed 128x128 image is
	//! trivial to rasterize, and a software driver gives byte-identical output on
	//! every machine, which is what makes golden-image comparison in CI possible
	//! at all. Hardware paths also cannot be trusted here: a DISPLAY variable may
	//! be set while no server listens, and some Mesa builds segfault inside
	//! eglInitialize while probing for a device in that state.
	//!
	//! An explicit LIBGL_ALWAYS_SOFTWARE is always respected, so a caller who
	//! wants the GPU can ask for it.
	static void PreferSoftwareRasterizer()
	{
		if (std::getenv("LIBGL_ALWAYS_SOFTWARE") != nullptr)
		{
			return;
		}
		setenv("LIBGL_ALWAYS_SOFTWARE", "1", 0);
	}

	void Shutdown()
	{
		if (display_ == EGL_NO_DISPLAY)
		{
			return;
		}
		eglMakeCurrent(display_, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
		if (context_ != EGL_NO_CONTEXT)
		{
			eglDestroyContext(display_, context_);
			context_ = EGL_NO_CONTEXT;
		}
		eglTerminate(display_);
		display_ = EGL_NO_DISPLAY;
	}

	EGLDisplay display_ = EGL_NO_DISPLAY;
	EGLContext context_ = EGL_NO_CONTEXT;
};


//! Upstream's preview code reports progress on stdout and mesh-loading trouble
//! on stderr. Neither belongs in this tool's output: the preview always tries to
//! load a sphere mesh even when rendering a flat screen quad, so a perfectly
//! successful run would otherwise complain about a missing .obj. Swallow both
//! streams while the preview is set up and replay them only on failure.
class CapturedOutput
{
public:
	CapturedOutput()
		: savedOut_(std::cout.rdbuf(buffer_.rdbuf()))
		, savedErr_(std::cerr.rdbuf(buffer_.rdbuf()))
	{
	}

	~CapturedOutput()
	{
		Restore();
	}

	void Restore()
	{
		if (savedOut_ != nullptr)
		{
			std::cout.rdbuf(savedOut_);
			savedOut_ = nullptr;
		}
		if (savedErr_ != nullptr)
		{
			std::cerr.rdbuf(savedErr_);
			savedErr_ = nullptr;
		}
	}

	std::string Text() const
	{
		return buffer_.str();
	}

private:
	std::ostringstream buffer_;
	std::streambuf* savedOut_ = nullptr;
	std::streambuf* savedErr_ = nullptr;
};

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

//! Reads the preview's colour attachment back into an RGBA8 buffer, flipping to
//! top-down order for image files.
bool ReadPixels(uint32_t glTexture, int32_t size, std::vector<uint8_t>& out, std::string& error)
{
	GLuint framebuffer = 0;
	glGenFramebuffers(1, &framebuffer);
	glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
	glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, glTexture, 0);

	if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
	{
		glBindFramebuffer(GL_FRAMEBUFFER, 0);
		glDeleteFramebuffers(1, &framebuffer);
		error = "could not read back the preview render target";
		return false;
	}

	std::vector<uint8_t> raw(static_cast<size_t>(size) * size * 4);
	glPixelStorei(GL_PACK_ALIGNMENT, 1);
	glReadPixels(0, 0, size, size, GL_RGBA, GL_UNSIGNED_BYTE, raw.data());

	glBindFramebuffer(GL_FRAMEBUFFER, 0);
	glDeleteFramebuffers(1, &framebuffer);

	// GL's origin is bottom-left; PNG rows run top-down.
	out.resize(raw.size());
	const size_t stride = static_cast<size_t>(size) * 4;
	for (int32_t y = 0; y < size; y++)
	{
		const auto* src = raw.data() + stride * static_cast<size_t>(size - 1 - y);
		std::copy(src, src + stride, out.data() + stride * static_cast<size_t>(y));
	}
	return true;
}

} // namespace

const std::string& LastDeviceDescription()
{
	return deviceDescription;
}

bool RenderMaterial(const std::shared_ptr<EffekseerMaterial::Material>& material,
					const RenderRequest& request,
					std::string& error)
{
	auto outputNode = FindOutputNode(material);
	if (outputNode == nullptr)
	{
		error = "the material has no output node";
		return false;
	}

	HeadlessGL gl;
	if (!gl.Initialize(error))
	{
		return false;
	}

	// Texture loading goes through the editor's IO layer; no file watching for
	// a one-shot batch run. Its IPC storage is an editor concern and warns when
	// absent, which is expected and uninteresting here.
	spdlog::set_level(spdlog::level::err);
	Effekseer::IO::Initialize(0);

	const int32_t size = EffekseerMaterial::Preview::TextureSize;

	auto graphics = std::make_shared<EffekseerMaterial::Graphics>();
	if (!graphics->Initialize(size, size))
	{
		error = "could not create an Effekseer OpenGL device";
		return false;
	}

	auto preview = std::make_shared<EffekseerMaterial::Preview>();

	CapturedOutput captured;
	const auto fail = [&captured, &error](std::string message) -> bool
	{
		captured.Restore();
		const auto chatter = captured.Text();
		error = std::move(message);
		if (!chatter.empty())
		{
			error += "\n" + chatter;
		}
		return false;
	};

	// Preview::Initialize loads its mesh from a path relative to the working
	// directory, so run it from the resource root when a shape is requested.
	std::error_code ec;
	const auto previousDirectory = std::filesystem::current_path(ec);
	const bool needsMesh = request.model != PreviewModel::Screen && !request.resourceDirectory.empty();
	if (needsMesh)
	{
		std::filesystem::current_path(request.resourceDirectory, ec);
		if (ec)
		{
			return fail("could not enter resource directory: " + request.resourceDirectory);
		}
	}

	const bool previewReady = preview->Initialize(graphics);

	if (needsMesh)
	{
		std::filesystem::current_path(previousDirectory, ec);
	}

	if (!previewReady)
	{
		return fail("could not initialize the material preview");
	}

	preview->ModelType = request.model == PreviewModel::Sphere ? EffekseerMaterial::PreviewModelType::Sphere
															  : EffekseerMaterial::PreviewModelType::Screen;

	auto compileResult = EffekseerMaterial::Compile(graphics, material, outputNode);

	if (!preview->UpdateShader(compileResult))
	{
		// This is the interesting failure: the graph exported, but the driver
		// rejected the generated shader. Surface it rather than writing an image.
		return fail("the generated shader failed to compile on this GL driver");
	}

	if (!preview->UpdateUniforms(compileResult.textures, compileResult.uniforms, compileResult.gradients))
	{
		return fail("could not bind the material's textures and uniforms");
	}

	// Feed the material's own custom data, as the editor does. Passing zeros here
	// silently blanks every material that scales anything by custom data.
	preview->UpdateConstantValues(request.time, material->CustomData[0].Values, material->CustomData[1].Values);
	preview->Render();
	captured.Restore();

	std::vector<uint8_t> pixels;
	if (!ReadPixels(static_cast<uint32_t>(preview->GetInternal()), size, pixels, error))
	{
		return false;
	}

	const auto parent = std::filesystem::path(request.outputPath).parent_path();
	if (!parent.empty())
	{
		std::filesystem::create_directories(parent, ec);
	}

	if (stbi_write_png(request.outputPath.c_str(), size, size, 4, pixels.data(), size * 4) == 0)
	{
		error = "could not write " + request.outputPath;
		return false;
	}

	return true;
}

} // namespace efkmatc
// [UAA] - END
