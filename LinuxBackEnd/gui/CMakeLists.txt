cmake_minimum_required(VERSION 3.22)
project(IPadTabletBackendGui VERSION 0.0.3 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

find_package(Qt6 REQUIRED COMPONENTS Widgets)

qt_add_executable(ipad-tablet-backend-gui
  main.cpp
  MainWindow.cpp
  MainWindow.h
)
target_link_libraries(ipad-tablet-backend-gui PRIVATE Qt6::Widgets)
target_compile_options(ipad-tablet-backend-gui PRIVATE
  $<$<CXX_COMPILER_ID:GNU,Clang>:-Wall;-Wextra;-Wpedantic>
)
target_compile_definitions(ipad-tablet-backend-gui PRIVATE
  IPAD_TABLET_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}/.."
)

include(GNUInstallDirs)
install(TARGETS ipad-tablet-backend-gui RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
