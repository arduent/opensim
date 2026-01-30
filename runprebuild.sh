#!/bin/sh

case "$1" in

 'clean')
    dotnet bin/prebuild.dll /file prebuild.xml /clean

  ;;


  'autoclean')

    echo y|dotnet bin/prebuild.dll /file prebuild.xml /clean

  ;;



  *)

    cp bin/System.Drawing.Common.dll.linux bin/System.Drawing.Common.dll
    dotnet bin/prebuild.dll /target vs2022 /targetframework net8_0 /excludedir = "obj | bin" /file prebuild.xml
    rm -rf addon-modules/ParentalControls.Region/obj
    rm -rf addon-modules/ParentalControls.Robust/obj
    rm -rf addon-modules/Gloebit/GloebitMoneyModule/obj
    echo "dotnet build -c Release OpenSim.sln" > compile.sh
    chmod +x compile.sh

  ;;

esac
