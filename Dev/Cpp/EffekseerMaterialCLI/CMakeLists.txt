# [UAA] - START - efkmatc headless material CLI
# Console tool over the material system: inspects .efkmat parameter interfaces
# and node graphs, validates them, exports generated shader source, produces
# .efkmatd compiled caches, edits graphs, and renders offscreen previews.
#
# It compiles the upstream sources it needs directly instead of linking the
# Viewer/EditorCommon targets. That is deliberate: linking EditorCommon would
# drag in imgui, GLFW and (via 3rdParty/Boxer) a hard GTK3 requirement, which
# would make a headless CI tool unbuildable on exactly the machines it is meant
# to run on. Only the file-IO and preview translation units are pulled in.
project(EffekseerMaterialCLI)

cmake_minimum_required(VERSION 3.15)

if(MSVC)
  option(USE_MSVC_RUNTIME_LIBRARY_DLL "Use MSVC runtime library DLL" OFF)
endif()

set(CMAKE_CXX_STANDARD 17)

set(efkmatc_srcs main.cpp ../Viewer/CompiledMaterialGenerator.cpp
                 ../Viewer/DynamicLinkLibrary.cpp)

# Offscreen preview rendering. The context comes from EGL's surfaceless
# platform, so no window and no display server are ever required. Only wired up
# where that platform exists; other hosts report the limitation at run time
# rather than shipping an untested context path.
if(UNIX AND NOT APPLE)
  find_library(EFKMATC_EGL_LIBRARY EGL)
  find_package(OpenGL)
endif()

set(EFKMATC_RENDER OFF)
if(UNIX
   AND NOT APPLE
   AND EFKMATC_EGL_LIBRARY
   AND OPENGL_FOUND
   AND TARGET EffekseerRendererGL)
  set(EFKMATC_RENDER ON)
endif()

if(EFKMATC_RENDER)
  list(APPEND efkmatc_srcs Render.cpp
              ../EffekseerMaterialEditor/Graphics/efkMat.Graphics.cpp
              ../EditorCommon/IO/IO.cpp ../EditorCommon/IO/StaticFile.cpp
              ../EditorCommon/IO/DefaultFileReader.cpp
              ../EditorCommon/IO/IPCFileReader.cpp
              ../EditorCommon/Platform/Linux/FileSystem.cpp
              ../EditorCommon/Platform/Linux/PlatformMisc.cpp)
endif()

add_executable(efkmatc ${efkmatc_srcs})

if(MSVC)
  # CMAKE_MSVC_RUNTIME_LIBRARY can be only used if minimum required is larger
  # than 3.17
  if(USE_MSVC_RUNTIME_LIBRARY_DLL)
    cmake_policy(SET CMP0091 NEW)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
  else()
    cmake_policy(SET CMP0091 NEW)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  endif()
  target_compile_definitions(efkmatc PRIVATE NOMINMAX)
endif()

target_include_directories(efkmatc PRIVATE ../EffekseerMaterial/ ../Viewer/)

target_link_libraries(efkmatc PRIVATE Effekseer EffekseerMaterial
                                      ${CMAKE_DL_LIBS})

if(EFKMATC_RENDER)
  target_compile_definitions(efkmatc PRIVATE EFKMATC_ENABLE_RENDER=1)
  # OpenGLExtensions.h is included only for its GL declarations; the preview code
  # calls core GL entry points, not the GLFW-based loader in OpenGLHelper. So the
  # vendored GLFW headers are enough and neither GLFW nor OpenGLHelper is linked.
  target_include_directories(
    efkmatc
    PRIVATE ../ ../include/ ../EffekseerMaterialEditor/ ../OpenGLHelper/
            ../EditorCommon/ ../3rdParty/spdlog/include/
            ../3rdParty/glfw/include/)
  target_link_libraries(efkmatc PRIVATE EffekseerRendererGL IPC
                                        ${EFKMATC_EGL_LIBRARY}
                                        ${OPENGL_LIBRARIES})
endif()

if(UNIX)
  find_package(Threads REQUIRED)
  target_link_libraries(efkmatc PRIVATE ${CMAKE_THREAD_LIBS_INIT})
endif()

set_target_properties(efkmatc PROPERTIES LINKER_LANGUAGE CXX FOLDER Tools)
# [UAA] - END - efkmatc headless material CLI
