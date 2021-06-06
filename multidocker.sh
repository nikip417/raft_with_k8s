#!/bin/bash
# eval $(minikube docker-env)
kubectl delete statefulset.apps/actordemo
dotnet publish -c Release
docker build -t actordemo .
kubectl apply -f cluster.yaml
#docker network create -d bridge --subnet 172.25.0.0/16 isolated_nw
# docker run --network isolated_nw --rm -itd --name actordemo-0 -e HOSTNAME=actordemo-0 actordemo
# docker run --network isolated_nw --rm -it --name actordemo-1 -e HOSTNAME=actordemo-1 actordemo
