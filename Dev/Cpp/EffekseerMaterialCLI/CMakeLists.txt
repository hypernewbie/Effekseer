# [UAA] - START - efkmatc headless material CLI
# Console tool over the material system: inspects .efkmat parameter interfaces
# and node graphs, validates them, exports generated shader source, and
# produces .efkmatd compiled caches. It reuses the Viewer's compiled-material
# driver sources directly rather than linking the Viewer target, so no
# imgui/GLFW/OpenGL dependency is pulled into a headless build.
project(EffekseerMaterialCLI)

cmake_minimum_required(VERSION 3.15)

if(MSVC)
  option(USE_MSVC_RUNTIME_LIBRARY_DLL "Use MSVC runtime library DLL" OFF)
endif()

set(CMAKE_CXX_STANDARD 17)

add_executable(efkmatc main.cpp ../Viewer/CompiledMaterialGenerator.cpp
                       ../Viewer/DynamicLinkLibrary.cpp)

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

if(UNIX)
  find_package(Threads REQUIRED)
  target_link_libraries(efkmatc PRIVATE ${CMAKE_THREAD_LIBS_INIT})
endif()

set_target_properties(efkmatc PROPERTIES LINKER_LANGUAGE CXX FOLDER Tools)
# [UAA] - END - efkmatc headless material CLI
