dotnet publish -c Release
docker build -t actordemo .
sudo docker run -it --rm --entrypoint dotnet actordemo /app/ActorDemo.dll
sudo docker rmi actordemo:latest
