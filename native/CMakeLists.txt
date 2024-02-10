cmake_minimum_required(VERSION 3.5)

project(wordcloud VERSION 1.0.0 DESCRIPTION "wordcloud utils")

set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)

add_library(wordcloud SHARED
    ./src/wordcloud.c
)

set_target_properties(wordcloud PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    PUBLIC_HEADER ./include/wordcloud.h
)

include(GNUInstallDirs)
install(TARGETS wordcloud
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    PUBLIC_HEADER DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
)
