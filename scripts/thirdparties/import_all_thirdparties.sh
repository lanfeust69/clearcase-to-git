#!/bin/sh

all_thirdparties=$1
if [ ! $all_thirdparties ] ; then all_thirdparties=all_thirdparties ; fi

while read line
do
    import_thirdparty.sh $line
done <$all_thirdparties

echo remember handling special thirdparties : DBInterface (moved from src) and SGRealDates
