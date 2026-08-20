cmake_minimum_required(VERSION 3.20)
project(PresettingwayAddon LANGUAGES CXX)

# Builds Presettingway.dll — rename to Presettingway.addon and drop it next to
# ffxiv_dx11.exe (or wherever your ReShade addon search path points) to install.

add_library(PresettingwayAddon SHARED
    dllmain.cpp
    ThirdParty/Sharingway/Sharingway.cpp
)

target_include_directories(PresettingwayAddon PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/reshade-sdk
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/Sharingway
)

target_compile_features(PresettingwayAddon PRIVATE cxx_std_17)

if (MSVC)
    target_compile_options(PresettingwayAddon PRIVATE /W3 /permissive-)
endif()

set_target_properties(PresettingwayAddon PROPERTIES
    OUTPUT_NAME "Presettingway"
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/bin
)

# kernel32/user32/advapi32 etc. are linked by MSVC by default; nothing extra needed
# beyond what Sharingway.cpp and the ReShade header-only API already pull in.
