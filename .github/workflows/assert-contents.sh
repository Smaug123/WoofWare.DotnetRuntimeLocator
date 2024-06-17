#!/bin/bash

mkdir from-nuget && cp from-nuget.nupkg from-nuget/zip.zip && cd from-nuget && unzip zip.zip && cd - || exit 1

mkdir from-local && cp packed/*.nupkg from-local/zip.zip && cd from-local && unzip zip.zip && cd - || exit 1

find from-local -type f -exec sha256sum {} \; | sort > from-local.txt
find from-nuget -type f -and -not -name '.signature.p7s' -exec sha256sum {} \; | sort > from-nuget.txt

diff from-local.txt from-nuget.txt
