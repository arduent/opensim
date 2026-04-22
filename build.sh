dotnet clean OpenSim.sln
dotnet bin/prebuild.dll /target vs2022 /targetframework net8_0 /excludedir="obj|bin" /file prebuild.xml
find . -name "*.csproj" -exec sed -i '/EnableUnsafeBinaryFormatterSerialization/d' {} \;
dotnet build -c Release OpenSim.sln

