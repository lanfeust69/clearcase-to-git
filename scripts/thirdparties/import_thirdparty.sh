#!/bin/sh

to_import=$1
labels_to_keep=$*
viewTag=MyView
view=M:\\$viewTag\\MyVob

echo importing thirdparty $1, keeping labels $labels_to_keep

if [ ! -d working/$to_import ]
then
    mkdir -p working/$to_import
fi

if [ -f GitImporter.log ]
then
    rm GitImporter.log
fi

cd working/$to_import

if [ -f prevDate ]
then
    refDate=`cat prevDate`
else
    refDate=`date +%d-%b-%Y.%H:%M:%S | tr '[a-z]' '[A-Z]'`
    echo $refDate >prevDate
fi

# setup view
cat >cs <<EOF
element * CHECKEDOUT

element * /main/LATEST -time $refDate
EOF

cleartool setcs -tag $viewTag cs
rm cs

dirs=$to_import.dirs
if [ ! -f $dirs ]
then
    cleartool find $view\\thirdparty\\$to_import -type d -print | perl -pe 's/.*\\MyVob\\//' >$dirs
fi

files=$to_import.files
if [ ! -f $files ]
then
    cleartool find $view\\thirdparty\\$to_import -type f -print | perl -pe 's/.*\\MyVob\\//' >$files
fi

vobData=$to_import.bin
if [ ! -f $vobData ]
then
    ../../GitImporter -S:$vobData -D:$dirs -E:$files -C:$view -G
    mv ../../GitImporter.log export_$to_import.log
fi

importData=$to_import.dat
if [ ! -f $importData ]
then
    ../../GitImporter -L:$vobData -N -R:thirdparty\\$to_import -H:history.bin -C:$view >$importData
    mv ../../GitImporter.log import_$to_import.log
    perl -pi.bak -e 's/thirdparty\/'$to_import'\///g if /^(M|R|D|C) /' $importData
fi

export GIT_DIR=../../../../../Git/thirdparty/$to_import.git
if [ ! -d $GIT_DIR ]
then
    git init --bare
    git config core.ignorecase false
    ../../GitImporter -C:$view -F:$importData | git fast-import --export-marks=$to_import.marks >git_import_$to_import.log
    git repack -a -f -d --window-memory=300m
    mv ../../GitImporter.log fetch_$to_import.log
fi

git tag | perl -e '@to_keep = @ARGV; while(<STDIN>) { chomp; $keep=0; foreach $pat (@to_keep) { $keep ||= /^$pat/i } if (!$keep) { print `git tag -d $_` } }' $labels_to_keep

configData=$to_import.config
echo "    <ThirdPartyModule>" >$configData
echo "      <Name>$to_import</Name>" >>$configData
echo "      <Labels>" >>$configData
git tag | perl -ne 'chomp; $commit = `git rev-parse $_^0`; chomp $commit; print "        <LabelMapping>\n          <Label>$_</Label>\n          <Commit>$commit</Commit>\n        </LabelMapping>\n"' >>$configData
echo "      </Labels>" >>$configData
echo "    </ThirdPartyModule>" >>$configData
