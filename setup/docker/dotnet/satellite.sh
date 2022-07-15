red=$'\e[0;31m'
green=$'\e[0;32m'
orange=$'\e[0;33m'
white=$'\e[0m'

echo "${green}Welcome to the satellite.sh script!${white}"
project=$2

if [ -f "../commit_changed" || ! -f "../build_successful" ]; then

    echo "Build project ${green}${project}${white}"
    dotnet build -c Release ${project}

    if [[ $? == 0 ]]; then
        touch "../build_successful"
    else
        echo "${red}Build failed${white}"
        rm --force "../build_successful"
    fi
fi

# run user code
shift
shift
echo "Run command ${green}dotnet run -c Release --no-build --project ${project} -- $@${white}"
dotnet run -c Release --no-build --project ${project} -- $@
