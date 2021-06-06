FROM mcr.microsoft.com/dotnet/sdk:5.0

# This Dockerfile is based heavily on https://github.com/petabridge/Cluster.WebCrawler/blob/dev/src/Lighthouse/Dockerfile

WORKDIR /app

ENV CLUSTER_SEEDS "[]"
ENV CLUSTER_IP ""
ENV CLUSTER_PORT "4053"


# 4053 is Akka.NET itself
# 9110 is Petabridge's Petabridge.Cmd (https://cmd.petabridge.com/articles/quickstart.html) which is
# an extension to the ActorSystem to view logs and such remotely
EXPOSE 9110 4053

RUN dotnet tool install --global pbm
ENV PATH="${PATH}:/root/.dotnet/tools"

# The copy is almost-last because we assume there will be many iterations of packaging, and this makes rebuilding with a new package faster
COPY bin/Release/net5.0/publish/ /app/
ENTRYPOINT ["dotnet", "/app/ActorDemo.dll"]

# Recall that in order to run this from inside minikube, this image must be built after evaluating the output from $(minikube docker-env), so that the image exists inside the minikube cluster controller; local existence on the *host* won't help kubernetes
# i.e., run: eval $(minikube docker-env)
# in your termainl first