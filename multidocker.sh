#!/bin/bash
# eval $(minikube docker-env)
kubectl delete statefulset.apps/raftnode
dotnet publish -c Release
docker build -t raftnode .
kubectl apply -f cluster.yaml
#docker network create -d bridge --subnet 172.25.0.0/16 isolated_nw
# docker run --network isolated_nw --rm -itd --name raftnode-0 -e HOSTNAME=raftnode-0 raftnode
# docker run --network isolated_nw --rm -it --name raftnode-1 -e HOSTNAME=raftnode-1 raftnode
