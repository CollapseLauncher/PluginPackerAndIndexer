@echo off
mkdir publish\Release
bflat build -o publish\Release\Indexer.exe -d BFLAT -Os --verbose --arch x64 --os windows --target Exe --no-stacktrace-data --no-globalization --no-exception-messages --no-pie --separate-symbols --no-debug-info --no-reflection --mstat