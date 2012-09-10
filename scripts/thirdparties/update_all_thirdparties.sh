#!/bin/sh

all_thirdparties=$1
if [ ! $all_thirdparties ] ; then all_thirdparties=all_thirdparties ; fi

while read line
do
    update_thirdparty.sh $line
done <$all_thirdparties

powershell update_config.ps1
if [ -f thirdparty.new.config ]
then
    n=1
    while [ -f thirdparty.config.$n ] ; do let n+=1 ; done
    mv thirdparty.config thirdparty.config.$n
    mv thirdparty.new.config thirdparty.config
    cp thirdparty.config ..
fi
