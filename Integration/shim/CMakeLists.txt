cmake_minimum_required(VERSION 3.20)
project(orvr_openxr_shim LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# OpenXR SDK: set OpenXR_ROOT to your SDK install (or install via vcpkg).
#   https://github.com/KhronosGroup/OpenXR-SDK/releases
if(DEFINED ENV{OpenXR_ROOT})
  set(OpenXR_ROOT $ENV{OpenXR_ROOT})
endif()

add_library(orvr_openxr_shim SHARED
  orvr_openxr_shim.cpp
  orvr_openxr_shim.h
)

target_include_directories(orvr_openxr_shim PRIVATE
  "${OpenXR_ROOT}/include"
)
target_link_directories(orvr_openxr_shim PRIVATE
  "${OpenXR_ROOT}/lib"
)
target_link_libraries(orvr_openxr_shim PRIVATE
  openxr_loader
  d3d11
)

# Output next to the C# build so RunActivity finds it without PATH changes:
# copy orvr_openxr_shim.dll beside RunActivity.exe (or anywhere on PATH).
set_target_properties(orvr_openxr_shim PROPERTIES
  OUTPUT_NAME orvr_openxr_shim
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
