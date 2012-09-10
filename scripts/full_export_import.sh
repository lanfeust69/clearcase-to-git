#!/bin/sh

refDate='22-AUG-2012.10:30:00'
viewTag=MyView
view=M:\\$viewTag\\MyVob
workingDir=`pwd`
workingDirWin=`pwd -W | perl -pe 's/\//\\\\/g'`

if [ ! -d export ]
then
    echo $refDate >prevDate
    mkdir export

    cat >cs <<EOF
element * CHECKEDOUT

element * /main/LATEST -time $refDate
EOF

    cleartool setcs -tag $viewTag cs
    rm cs

    cd /m/$viewTag/MyVob

    echo finding directories...
    cleartool find -all -type d -print >$workingDir/export/all_dirs
    echo finding files...
    cleartool find -all -type f -print >$workingDir/export/all_files

    # one clearexport_ccase for each directory, do that 1) it doesn't crash (out of memory), 2) it is parallelized
    for d in *
    do
        if [ -d $d -a ! -f $workingDir/export/$d.export ]
        then
            echo exporting src/$d...
            clearexport_ccase -r -o $workingDirWin\\export\\$d.export $d >$workingDir/export/$d.export.log &
            sleep 5 # give it a bit of time to start
        fi
    done

    cd $workingDir/export
    working=1
    while [ $working ]
    do
        sleep 60
        working=
        echo -n waiting for
        for f in *.export
        do
            # clearcase export files are empty until everything is finished
            if [ ! -s $f ]
            then
                echo -n \ `basename $f .export`
                working=1
            fi
        done
        echo
    done

    perl ../filter.pl all_dirs >to_import.dirs
    perl ../filter.pl all_files >to_import.files

    ../GitImporter.exe -S:fullVobDB.bin -E:to_import.files -D:to_import.dirs -C:$view -O:$refDate -G *.export
    # ../GitImporter.exe -L:fullVobDB.bin.export_oid -S:fullVobDB.bin -E:to_import.files -D:to_import.dirs -C:$view -O:$refDate -G

    mv ../GitImporter.log build_vobdb.log

    cd ..
    ln export/fullVobDB.bin fullVobDB.bin
fi

if [ ! -f fullVobDB.bin ]
then
    echo file fullVobDB.bin not found
    exit 1
fi

GitImporter.exe -L:fullVobDB.bin -I:gitignore -T:thirdparty.config -H:history.bin -C:$view -N >to_import_full

mv GitImporter.log create_changesets.log

export GIT_DIR=MyGitRepo.git
git init --bare $GIT_DIR
git config core.ignorecase false

GitImporter.exe -T:thirdparty.config -C:$view -F:to_import_full | git fast-import --export-marks=MyGitRepo.marks

mv GitImporter.log create_repo.log

echo repacking repo...
git repack -a -d -f --window-memory=50m
